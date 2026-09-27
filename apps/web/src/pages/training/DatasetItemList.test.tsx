import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { DatasetItemList } from './DatasetItemList'
import type { WaveformLoader, WaveformLoadOptions } from './waveformLoader'
import type { components } from '../../api/schema-training'
import * as client from '../../api/client'

type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingDatasetItems: vi.fn(),
    updateTrainingDatasetItem: vi.fn(),
    batchDeleteTrainingDatasetItems: vi.fn(),
  }
})

const mockListItems = vi.mocked(client.listTrainingDatasetItems)
const mockUpdateItem = vi.mocked(client.updateTrainingDatasetItem)
const mockBatchDelete = vi.mocked(client.batchDeleteTrainingDatasetItems)

const baseItem = {
  audioType: 'transcript',
  audioUri: 'b/live/101/recordings/a.flv',
  extensions: null,
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
  })

  it('should show the seven fields and keep the audio key read-only', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })

    await screen.findByText('第一句')

    for (const column of COLUMNS) {
      expect(screen.getByRole('columnheader', { name: column })).toBeInTheDocument()
    }
    expect(screen.getByText('b/live/101/recordings/a.flv')).toBeInTheDocument()
    expect(screen.getAllByText('transcript')).toHaveLength(3)
    expect(screen.getAllByText('t1')).toHaveLength(2)
    expect(screen.getByText('0:00')).toBeInTheDocument()
    expect(screen.getByText('1:00')).toBeInTheDocument()
    expect(screen.getByText('-')).toBeInTheDocument()

    const cell = screen.getByText('transcript:t1:0').closest('td')
    expect(cell).not.toBeNull()
    expect(cell?.querySelector('input')).toBeNull()
    expect(screen.queryByTestId('waveform-container')).not.toBeInTheDocument()
  })

  it('should constrain the low-value columns and let the text column fill the remaining width', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    const table = screen.getByRole('table')
    expect(table.style.width).toBe('100%')
    expect(table.style.borderCollapse).toBe('collapse')
    // 固定表格布局：次要列限宽后不会撑宽表格
    expect(table.style.tableLayout).toBe('fixed')

    // 来源/地址/标识三列：限宽 + 省略号截断（不绑死具体像素值，仅断言样式契约）
    for (const column of ['音频来源', '音频地址', '音频标识']) {
      const header = screen.getByRole('columnheader', { name: column })
      expect(header.style.maxWidth).not.toBe('')
      expect(header.style.overflow).toBe('hidden')
      expect(header.style.textOverflow).toBe('ellipsis')
      expect(header.style.whiteSpace).toBe('nowrap')
    }

    // 数据单元格同样截断，并以 title 悬浮展示完整值
    const uriCell = screen.getByText('b/live/101/recordings/a.flv')
    expect(uriCell.style.textOverflow).toBe('ellipsis')
    expect(uriCell).toHaveAttribute('title', 'b/live/101/recordings/a.flv')
    const keyCell = screen.getByText('transcript:t1:0')
    expect(keyCell.style.overflow).toBe('hidden')
    expect(keyCell).toHaveAttribute('title', 'transcript:t1:0')

    // 「文本」列不设 max-width，正常换行展示全文
    const textHeader = screen.getByRole('columnheader', { name: '文本' })
    expect(textHeader.style.maxWidth).toBe('')
    expect(textHeader.style.whiteSpace).not.toBe('nowrap')

    // 仅「文本」列对应的 col 不设宽度 → 占满剩余宽度
    const cols = table.querySelectorAll('colgroup col')
    expect(cols).toHaveLength(9)
    expect((cols[7] as HTMLElement).style.width).toBe('')
    expect((cols[3] as HTMLElement).style.width).not.toBe('')
  })

  it('should use a wrapping textarea that fills its cell for the text editor', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    editRow('transcript:t1:0')

    // 「文本」编辑态为多行 textarea 且占满单元格（aria-label 保持不变）
    const textEditor = screen.getByLabelText('文本') as HTMLTextAreaElement
    expect(textEditor.tagName).toBe('TEXTAREA')
    expect(textEditor.rows).toBeGreaterThan(1)
    expect(textEditor.style.width).toBe('100%')
    expect(textEditor.style.boxSizing).toBe('border-box')

    // 编辑态「音频地址」等输入同样 width:100% + box-sizing，不撑破列宽约束
    const uriEditor = screen.getByLabelText('音频地址') as HTMLInputElement
    expect(uriEditor.style.width).toBe('100%')
    expect(uriEditor.style.boxSizing).toBe('border-box')
  })

  it('should edit audio uri, text and the m:ss positions and save them as milliseconds', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

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

    editRow('transcript:t1:0')
    fireEvent.change(screen.getByLabelText('开始时间'), { target: { value: '0:10' } })
    fireEvent.change(screen.getByLabelText('结束时间'), { target: { value: '0:05' } })
    saveRow()

    expect(await screen.findByText('时间输入无效')).toBeInTheDocument()
    expect(mockUpdateItem).not.toHaveBeenCalled()
  })

  it('should batch delete the checked items after a confirm and refresh the list', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(
      screen.getByRole('checkbox', { name: '选择 transcript:t1:61000' }),
    )
    fireEvent.click(screen.getByRole('button', { name: '批量删除' }))

    expect(confirmSpy).toHaveBeenCalledWith(expect.stringContaining('删除'))
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
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(screen.getByRole('button', { name: '批量删除' }))

    expect(mockBatchDelete).not.toHaveBeenCalled()
    expect(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' })).toBeChecked()
  })

  it('should switch between 不分组, 按来源 and 按类型 groupings', async () => {
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    expect(screen.queryByText('来源: t1（2 条）')).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('分组方式'), { target: { value: 'source' } })
    expect(await screen.findByText('来源: t1（2 条）')).toBeInTheDocument()
    expect(screen.getByText('来源: t2（1 条）')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('分组方式'), {
      target: { value: 'audioType' },
    })
    expect(await screen.findByText('类型: transcript（3 条）')).toBeInTheDocument()
    expect(screen.queryByText('来源: t1（2 条）')).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('分组方式'), { target: { value: 'none' } })
    expect(screen.queryByText('类型: transcript（3 条）')).not.toBeInTheDocument()
  })

  it('should select a whole group from the group header and batch delete it', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const { loader } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.change(screen.getByLabelText('分组方式'), { target: { value: 'source' } })
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

    expect(confirmSpy).toHaveBeenCalledWith(expect.stringContaining('删除'))
    await waitFor(() => {
      expect(mockBatchDelete).toHaveBeenCalledWith('d1', ['i1', 'i2'])
    })
  })

  it('should keep the list operations usable when an item has no playable audio', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const { loader, load } = createLoader()
    renderList({ waveformLoader: loader })
    await screen.findByText('第一句')

    fireEvent.click(screen.getByText('transcript:t2:5000'))

    expect(await screen.findByText('音频不可用')).toBeInTheDocument()
    expect(load).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('checkbox', { name: '选择 transcript:t1:0' }))
    fireEvent.click(screen.getByRole('button', { name: '批量删除' }))

    expect(confirmSpy).toHaveBeenCalledWith(expect.stringContaining('删除'))
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
})
