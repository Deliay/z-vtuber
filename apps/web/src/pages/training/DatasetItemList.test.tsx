import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor, within } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { DatasetItemList } from './DatasetItemList'
import type { WaveformLoader, WaveformLoadOptions } from './waveformLoader'
import type { components } from '../../api/schema-training'
import * as client from '../../api/client'

type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']
type TrainingModel = components['schemas']['TrainingModel']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingDatasetItems: vi.fn(),
    updateTrainingDatasetItem: vi.fn(),
    batchDeleteTrainingDatasetItems: vi.fn(),
    listTrainingModels: vi.fn(),
    recognizeTrainingDatasetItem: vi.fn(),
    createEmotionTasks: vi.fn(),
  }
})

const mockListItems = vi.mocked(client.listTrainingDatasetItems)
const mockUpdateItem = vi.mocked(client.updateTrainingDatasetItem)
const mockBatchDelete = vi.mocked(client.batchDeleteTrainingDatasetItems)
const mockListModels = vi.mocked(client.listTrainingModels)
const mockRecognize = vi.mocked(client.recognizeTrainingDatasetItem)
const mockCreateEmotionTasks = vi.mocked(client.createEmotionTasks)

const baseItem = {
  audioType: 'transcript',
  audioUri: 'b/live/101/recordings/a.flv',
  extensions: null,
  emotion: null,
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
  datasetId: 'd1',
}

const items: TrainingDatasetItem[] = [
  {
    ...baseItem,
    id: 'i1',
    source: 't1',
    audioKey: 'transcript:t1:0',
    startMs: 0,
    endMs: 60000,
    text: '第一句',
  },
  {
    ...baseItem,
    id: 'i2',
    source: 't1',
    audioUri: 'b/live/101/recordings/b.flv',
    audioKey: 'transcript:t1:61000',
    startMs: 61000,
    endMs: null,
    text: '第二句',
  },
  {
    ...baseItem,
    id: 'i3',
    source: 't2',
    audioUri: '',
    audioKey: 'transcript:t2:5000',
    startMs: 5000,
    endMs: 30000,
    text: '第三句',
  },
]

const model: TrainingModel = {
  id: 'm1',
  providerId: 'p1',
  providerName: 'openai-prod',
  model: 'gpt-4o',
  slug: 'openai-prod/gpt-4o',
  enabled: true,
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
}

function createLoader() {
  const play = vi.fn()
  const stop = vi.fn()
  const destroy = vi.fn()
  const load = vi.fn((options: WaveformLoadOptions) => {
    options.onReady?.()
    return { play, stop, destroy }
  })
  const loader: WaveformLoader = { load }
  return { loader, load, play, stop, destroy }
}

const COLUMNS = [
  '音频类型',
  '音频来源',
  '音频地址',
  '音频标识',
  '开始时间',
  '结束时间',
  '文本',
]

function renderList(props: { waveformLoader?: WaveformLoader } = {}) {
  render(<DatasetItemList datasetId="d1" {...props} />)
}

function editRow(audioKey: string): void {
  fireEvent.click(screen.getByRole('button', { name: `编辑 ${audioKey}` }))
}

function saveRow(): void {
  fireEvent.click(screen.getByRole('button', { name: '保存' }))
}

/** 默认隐藏「音频信息」六列；需要操作音频列时显式打开。 */
function enableAudioInfo(): void {
  fireEvent.click(screen.getByLabelText('音频信息'))
}

/** 打开 antd Select 并按选项文本（title）选择。 */
async function pickOption(ariaLabel: string, optionText: string, scope?: HTMLElement) {
  fireEvent.mouseDown(within(scope ?? document.body).getByRole('combobox', { name: ariaLabel }))
  await waitFor(() => {
    expect(document.querySelectorAll('.ant-select-item-option').length).toBeGreaterThan(0)
  })
  const option = Array.from(document.querySelectorAll('.ant-select-item-option')).find((el) =>
    (el.getAttribute('title') ?? '').includes(optionText),
  )
  if (!option) throw new Error(`option not found: ${optionText}`)
  fireEvent.click(option)
}

/** 确认 antd Modal.confirm 的删除（okText「确认删除」）。 */
async function confirmDelete() {
  fireEvent.click(await screen.findByRole('button', { name: '确认删除' }))
}

describe('DatasetItemList', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListItems.mockResolvedValue(items)
    mockUpdateItem.mockImplementation(async (_datasetId, _itemId, params) => ({
      ...items[0],
      ...params,
      endMs: params.endMs === undefined ? items[0].endMs : params.endMs,
    }))
    mockBatchDelete.mockResolvedValue({ deleted: 2 })
    mockListModels.mockResolvedValue([model])
    mockCreateEmotionTasks.mockResolvedValue({ created: 1, skipped: 0 })
  })

  it('should show the seven fields and keep the audio key read-only', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })

    await screen.findByText('第一句')
    enableAudioInfo()

    for (const column of COLUMNS) {
      expect(screen.getByRole('columnheader', { name: column })).toBeInTheDocument()
    }
    expect(screen.getByText('b/live/101/recordings/a.flv')).toBeInTheDocument()
    expect(screen.getAllByText('transcript')).toHaveLength(3)
    expect(screen.getAllByText('t1')).toHaveLength(2)
    expect(screen.getByText('0:00')).toBeInTheDocument()
    expect(screen.getByText('1:00')).toBeInTheDocument()
    // 空结束时间、空情绪字段均展示 '-'
    expect(screen.getAllByText('-').length).toBeGreaterThan(0)

    const cell = screen.getByText('transcript:t1:0').closest('td')
    expect(cell).not.toBeNull()
    expect(cell?.querySelector('input')).toBeNull()
    expect(screen.queryByTestId('waveform-container')).not.toBeInTheDocument()
  })

  it('should constrain the low-value columns and let the text column fill the remaining width', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    const table = screen.getByRole('table')
    // 固定表格布局：次要列限宽后不会撑宽表格
    expect(table.style.tableLayout).toBe('fixed')

    // 来源/地址/标识三列：限宽 + 省略号截断（antd 以 ellipsis class + title 实现）
    for (const column of ['音频来源', '音频地址', '音频标识']) {
      const header = screen.getByRole('columnheader', { name: column })
      expect(header.className).toContain('ant-table-cell-ellipsis')
    }

    // 数据单元格同样截断，并以 title 悬浮展示完整值
    const uriCell = screen.getByText('b/live/101/recordings/a.flv')
    expect(uriCell.className).toContain('ant-table-cell-ellipsis')
    expect(uriCell).toHaveAttribute('title', 'b/live/101/recordings/a.flv')
    const keyCell = screen.getByText('transcript:t1:0')
    expect(keyCell.className).toContain('ant-table-cell-ellipsis')
    expect(keyCell).toHaveAttribute('title', 'transcript:t1:0')

    // 「文本」列不截断，正常换行展示全文
    const textHeader = screen.getByRole('columnheader', { name: '文本' })
    expect(textHeader.className).not.toContain('ant-table-cell-ellipsis')

    // 仅「文本」列对应的 col 不设宽度 → 占满剩余宽度（1 勾选 + 6 音频 + 1 文本 + 10 情绪 + 1 操作）
    const cols = table.querySelectorAll('colgroup col')
    expect(cols).toHaveLength(19)
    expect((cols[7] as HTMLElement).style.width).toBe('')
    expect((cols[3] as HTMLElement).style.width).not.toBe('')
  })

  it('should use a wrapping textarea that fills its cell for the text editor', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    editRow('transcript:t1:0')

    // 「文本」编辑态为多行 textarea（aria-label 保持不变），antd autoSize 负责撑满与换行
    const textEditor = screen.getByLabelText('文本') as HTMLTextAreaElement
    expect(textEditor.tagName).toBe('TEXTAREA')
    expect(textEditor.className).toContain('ant-input')
    expect(textEditor.style.minHeight).not.toBe('')

    // 编辑态「音频地址」等输入同样由 antd 撑满单元格，不撑破列宽约束
    const uriEditor = screen.getByLabelText('音频地址') as HTMLInputElement
    expect(uriEditor.className).toContain('ant-input')
  })

  it('should edit audio uri, text and the m:ss positions and save them as milliseconds', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    editRow('transcript:t1:0')
    expect(screen.getAllByRole('textbox')).toHaveLength(4)

    fireEvent.change(screen.getByLabelText('音频地址'), {
      target: { value: 'b/live/101/recordings/b.flv' },
    })
    fireEvent.change(screen.getByLabelText('文本'), { target: { value: '修订后' } })
    fireEvent.change(screen.getByLabelText('开始时间'), { target: { value: '1:30' } })
    fireEvent.change(screen.getByLabelText('结束时间'), { target: { value: '1:31.5' } })
    saveRow()

    await waitFor(() => {
      expect(mockUpdateItem).toHaveBeenCalledWith('d1', 'i1', {
        audioUri: 'b/live/101/recordings/b.flv',
        text: '修订后',
        startMs: 90000,
        endMs: 91500,
      })
    })
    expect(screen.queryByText('时间输入无效')).not.toBeInTheDocument()
  })

  it('should clear the end position by saving an empty value as endMs null', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    editRow('transcript:t1:0')
    fireEvent.change(screen.getByLabelText('结束时间'), { target: { value: '' } })
    saveRow()

    await waitFor(() => {
      expect(mockUpdateItem).toHaveBeenCalledWith('d1', 'i1', {
        audioUri: 'b/live/101/recordings/a.flv',
        text: '第一句',
        startMs: 0,
        endMs: null,
      })
    })
  })

  it('should reject a negative start position without saving', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    editRow('transcript:t1:0')
    fireEvent.change(screen.getByLabelText('开始时间'), { target: { value: '-0:05' } })
    saveRow()

    expect(await screen.findByText('时间输入无效')).toBeInTheDocument()
    expect(mockUpdateItem).not.toHaveBeenCalled()
  })

  it('should reject an end position that is not after the start position without saving', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    editRow('transcript:t1:0')
    fireEvent.change(screen.getByLabelText('开始时间'), { target: { value: '0:10' } })
    fireEvent.change(screen.getByLabelText('结束时间'), { target: { value: '0:05' } })
    saveRow()

    expect(await screen.findByText('时间输入无效')).toBeInTheDocument()
    expect(mockUpdateItem).not.toHaveBeenCalled()
  })

  it('should batch delete the checked items after a confirm and refresh the list', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(
      screen.getByRole('checkbox', { name: '选择 transcript:t1:61000' }),
    )
    fireEvent.click(screen.getByRole('button', { name: '批量删除' }))

    expect(await screen.findByText(/删除后不可恢复/)).toBeInTheDocument()
    await confirmDelete()
    await waitFor(() => {
      expect(mockBatchDelete).toHaveBeenCalledWith('d1', ['i1', 'i2'])
    })
    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledTimes(2)
    })
    expect(
      screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }),
    ).not.toBeChecked()
  })

  it('should not batch delete when the confirm is rejected', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(screen.getByRole('button', { name: '批量删除' }))
    fireEvent.click(await screen.findByRole('button', { name: '取消' }))

    expect(mockBatchDelete).not.toHaveBeenCalled()
    expect(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' })).toBeChecked()
  })

  it('should switch between 不分组, 按来源 and 按类型 groupings', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    expect(screen.queryByText('来源: t1（2 条）')).not.toBeInTheDocument()

    await pickOption('分组方式', '按来源')
    expect(await screen.findByText('来源: t1（2 条）')).toBeInTheDocument()
    expect(screen.getByText('来源: t2（1 条）')).toBeInTheDocument()

    await pickOption('分组方式', '按类型')
    expect(await screen.findByText('类型: transcript（3 条）')).toBeInTheDocument()
    expect(screen.queryByText('来源: t1（2 条）')).not.toBeInTheDocument()

    await pickOption('分组方式', '不分组')
    expect(screen.queryByText('类型: transcript（3 条）')).not.toBeInTheDocument()
  })

  it('should expand the waveform in a row right below the clicked item, inside the table', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    fireEvent.click(screen.getByText('transcript:t1:0'))

    const container = await screen.findByTestId('waveform-container')
    const expandRow = container.closest('tr')
    const dataRow = screen.getByText('transcript:t1:0').closest('tr')
    expect(expandRow).not.toBeNull()
    // 紧贴被点击条目行正下方，且位于表格内（而非表格后的兄弟节点）
    expect(expandRow?.previousElementSibling).toBe(dataRow)
    expect(screen.getByRole('table').contains(expandRow)).toBe(true)
    // 展开行以 colSpan 覆盖当前可见列（1 + 6 + 1 + 10 + 1 = 19）
    expect(expandRow?.querySelector('td')).toHaveAttribute('colspan', '19')
  })

  it('should compute the expanded row colSpan from the visible column count', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    // 默认「音频信息」隐藏：1 勾选 + 0 音频 + 1 文本 + 10 情绪 + 1 操作 = 13
    fireEvent.click(screen.getByText('第一句').closest('tr') as HTMLElement)
    const container = await screen.findByTestId('waveform-container')
    expect(container.closest('tr')?.querySelector('td')).toHaveAttribute('colspan', '13')
  })

  it('should keep only one waveform expanded at a time when switching items', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    fireEvent.click(screen.getByText('transcript:t1:0'))
    await screen.findByTestId('waveform-container')
    expect(screen.getAllByTestId('waveform-container')).toHaveLength(1)

    fireEvent.click(screen.getByText('transcript:t1:61000'))

    await waitFor(() =>
      expect(screen.getAllByTestId('waveform-container')).toHaveLength(1),
    )
    const expandRow = screen.getByTestId('waveform-container').closest('tr')
    const dataRow = screen.getByText('transcript:t1:61000').closest('tr')
    expect(expandRow?.previousElementSibling).toBe(dataRow)
  })

  it('should expand the waveform under the clicked row in group mode too', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    await pickOption('分组方式', '按来源')
    await screen.findByText('来源: t1（2 条）')

    fireEvent.click(screen.getByText('transcript:t1:61000'))

    const container = await screen.findByTestId('waveform-container')
    const expandRow = container.closest('tr')
    const dataRow = screen.getByText('transcript:t1:61000').closest('tr')
    expect(expandRow?.previousElementSibling).toBe(dataRow)
  })

  it('should select a whole group from the group header and batch delete it', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    await pickOption('分组方式', '按来源')
    await screen.findByText('来源: t1（2 条）')

    fireEvent.click(screen.getByRole('checkbox', { name: '全选分组 t1' }))

    expect(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' })).toBeChecked()
    expect(
      screen.getByRole('checkbox', { name: '选择 transcript:t1:61000' }),
    ).toBeChecked()
    expect(
      screen.getByRole('checkbox', { name: '选择 transcript:t2:5000' }),
    ).not.toBeChecked()

    fireEvent.click(screen.getByRole('button', { name: '批量删除' }))

    expect(await screen.findByText(/删除后不可恢复/)).toBeInTheDocument()
    await confirmDelete()
    await waitFor(() => {
      expect(mockBatchDelete).toHaveBeenCalledWith('d1', ['i1', 'i2'])
    })
  })

  it('should keep the list operations usable when an item has no playable audio', async () => {
    const { loader, load } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')
    enableAudioInfo()

    fireEvent.click(screen.getByText('transcript:t2:5000'))

    expect(await screen.findByText('音频不可用')).toBeInTheDocument()
    expect(load).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(screen.getByRole('button', { name: '批量删除' }))

    expect(await screen.findByText(/删除后不可恢复/)).toBeInTheDocument()
    await confirmDelete()
    await waitFor(() => {
      expect(mockBatchDelete).toHaveBeenCalledWith('d1', ['i1'])
    })
  })

  it('should show the empty state that guides the user to import from Transcript', async () => {
    mockListItems.mockResolvedValue([])
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })

    expect(
      await screen.findByText('暂无音频数据，可从 Transcript 导入'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('should render the 展示类型 toggles before the grouping select with the required defaults', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    const audioToggle = screen.getByLabelText('音频信息')
    const emotionToggle = screen.getByLabelText('情绪信息')
    const groupSelect = screen.getByLabelText('分组方式')

    // 默认：音频信息不勾选、情绪信息勾选
    expect(audioToggle).not.toBeChecked()
    expect(emotionToggle).toBeChecked()
    // 「展示类型」勾选组位于分组切换控件之前
    expect(
      audioToggle.compareDocumentPosition(groupSelect) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy()
  })

  it('should toggle the six audio columns and keep 文本/操作 always visible', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    // 默认音频信息隐藏
    expect(screen.queryByRole('columnheader', { name: '音频类型' })).not.toBeInTheDocument()
    // 文本与操作恒展示
    expect(screen.getByRole('columnheader', { name: '文本' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: '操作' })).toBeInTheDocument()

    enableAudioInfo()
    expect(screen.getByRole('columnheader', { name: '音频类型' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: '结束时间' })).toBeInTheDocument()

    enableAudioInfo()
    expect(screen.queryByRole('columnheader', { name: '音频类型' })).not.toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: '文本' })).toBeInTheDocument()
  })

  it('should toggle the ten emotion columns', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    for (const column of ['情绪控制权重', '情绪描述', '开心', '平静']) {
      expect(screen.getByRole('columnheader', { name: column })).toBeInTheDocument()
    }

    fireEvent.click(screen.getByLabelText('情绪信息'))

    for (const column of ['情绪控制权重', '情绪描述', '开心', '平静']) {
      expect(screen.queryByRole('columnheader', { name: column })).not.toBeInTheDocument()
    }
  })

  it('should show 文本 and 操作 even when both display groups are hidden', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByLabelText('情绪信息'))
    enableAudioInfo()

    expect(screen.getByRole('columnheader', { name: '文本' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: '操作' })).toBeInTheDocument()
  })

  it('should render a dash for the ten emotion columns when a row has no emotion data', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    const row = screen.getByText('第一句').closest('tr') as HTMLElement
    expect(within(row).getAllByText('-')).toHaveLength(10)
  })

  it('should render emotion values with a truncated description carrying the full title', async () => {
    const emotionItem: TrainingDatasetItem = {
      ...items[0],
      id: 'ie',
      audioKey: 'transcript:te:0',
      text: '情绪句',
      emotion: {
        emotionControlWeight: 0.6,
        emotionDescription: '克制的失落，语调低缓',
        happy: 0.05,
        angry: 0,
        sad: 0.7,
        afraid: 0.05,
        disgusted: 0,
        melancholic: 0.8,
        surprised: 0.1,
        calm: 0.4,
      },
    }
    mockListItems.mockResolvedValue([emotionItem])
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })

    await screen.findByText('情绪句')

    const description = screen.getByText('克制的失落，语调低缓')
    expect(description).toHaveAttribute('title', '克制的失落，语调低缓')
    expect(description.className).toContain('ant-table-cell-ellipsis')
    expect(screen.getByText('0.6')).toBeInTheDocument()
    expect(screen.getByText('0.7')).toBeInTheDocument()
  })

  it('should open the re-recognize dialog from the edit state', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    editRow('transcript:t1:0')
    fireEvent.click(screen.getByRole('button', { name: '重新识别' }))

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    await waitFor(() => {
      expect(mockListModels).toHaveBeenCalledWith(true)
    })
  })

  it('should submit the inline draft together with the re-recognized text so no edit is lost', async () => {
    const { loader } = createLoader()
    mockRecognize.mockResolvedValue({ text: '识别后的文本' })
    mockUpdateItem.mockResolvedValue({ ...items[0], text: '识别后的文本' })
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    // 进入内联编辑并留下未保存的文本修改
    editRow('transcript:t1:0')
    fireEvent.change(screen.getByLabelText('文本'), { target: { value: '过期草稿' } })
    fireEvent.click(screen.getByRole('button', { name: '重新识别' }))

    const dialog = await screen.findByRole('dialog')
    await pickOption('识别模型', 'openai-prod/gpt-4o', dialog)
    fireEvent.click(within(dialog).getByRole('button', { name: '识别' }))
    await waitFor(() => {
      expect(within(dialog).getByLabelText('文本')).toHaveValue('识别后的文本')
    })
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))

    // 保存与内联保存同 payload：audioUri/startMs/endMs 一并与识别文本提交，不再静默丢弃
    await waitFor(() => {
      expect(mockUpdateItem).toHaveBeenCalledWith('d1', 'i1', {
        audioUri: 'b/live/101/recordings/a.flv',
        text: '识别后的文本',
        startMs: 0,
        endMs: 60000,
      })
    })
    // 保存成功后关闭对话框并退出该行内联编辑，旧草稿不再可回写
    await waitFor(
      () => {
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      },
      { timeout: 3000 },
    )
    expect(screen.queryByLabelText('文本')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '保存' })).not.toBeInTheDocument()
    expect(screen.getByText('识别后的文本')).toBeInTheDocument()
  })

  it('should submit modified audio uri and time range from the inline draft on re-recognize save', async () => {
    const { loader } = createLoader()
    mockRecognize.mockResolvedValue({ text: '识别后的文本' })
    mockUpdateItem.mockResolvedValue({ ...items[0], text: '识别后的文本' })
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByRole('checkbox', { name: '音频信息' }))
    editRow('transcript:t1:0')
    fireEvent.change(screen.getByLabelText('音频地址'), {
      target: { value: 'b/live/101/recordings/z.flv' },
    })
    fireEvent.change(screen.getByLabelText('开始时间'), { target: { value: '2:00' } })
    fireEvent.change(screen.getByLabelText('结束时间'), { target: { value: '3:00' } })
    fireEvent.click(screen.getByRole('button', { name: '重新识别' }))

    const dialog = await screen.findByRole('dialog')
    await pickOption('识别模型', 'openai-prod/gpt-4o', dialog)
    fireEvent.click(within(dialog).getByRole('button', { name: '识别' }))
    await waitFor(() => {
      expect(within(dialog).getByLabelText('文本')).toHaveValue('识别后的文本')
    })
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))

    await waitFor(() => {
      expect(mockUpdateItem).toHaveBeenCalledWith('d1', 'i1', {
        audioUri: 'b/live/101/recordings/z.flv',
        text: '识别后的文本',
        startMs: 120000,
        endMs: 180000,
      })
    })
  })

  it('should disable 情绪识别 until something is selected and open the submit dialog', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    const emotionButton = screen.getByRole('button', { name: '情绪识别' })
    expect(emotionButton).toBeDisabled()
    expect(emotionButton).toHaveAttribute('aria-disabled', 'true')

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))

    expect(emotionButton).not.toBeDisabled()
    expect(emotionButton).toHaveAttribute('aria-disabled', 'false')

    fireEvent.click(emotionButton)
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
  })

  it('should submit emotion tasks for the selected items and refresh the list', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:61000' }))
    fireEvent.click(screen.getByRole('button', { name: '情绪识别' }))

    await pickOption('情绪识别模型', 'openai-prod/gpt-4o')
    fireEvent.click(screen.getByRole('button', { name: '提交' }))

    await waitFor(() => {
      expect(mockCreateEmotionTasks).toHaveBeenCalledWith('d1', ['i1', 'i2'], 'm1')
    })
    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledTimes(2)
    })
  })

  it('should notify the parent when the list changes after an emotion submission', async () => {
    const onChanged = vi.fn()
    const { loader } = createLoader()
    render(<DatasetItemList datasetId="d1" waveformLoader={loader} onChanged={onChanged} />)
    await screen.findByText('第一句')

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(screen.getByRole('button', { name: '情绪识别' }))
    await pickOption('情绪识别模型', 'openai-prod/gpt-4o')
    fireEvent.click(screen.getByRole('button', { name: '提交' }))

    await waitFor(() => {
      expect(onChanged).toHaveBeenCalled()
    })
  })
})
