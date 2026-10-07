import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { DatasetsPage } from './DatasetsPage'
import * as client from '../../api/client'

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingDatasets: vi.fn(),
    createTrainingDataset: vi.fn(),
    renameTrainingDataset: vi.fn(),
    deleteTrainingDataset: vi.fn(),
    listTrainingDatasetItems: vi.fn(),
    listTranscriptTasks: vi.fn(),
    importTrainingDatasetItems: vi.fn(),
    listTrainingProviders: vi.fn(),
    listTrainingModels: vi.fn(),
    listTrainingFeaturePoints: vi.fn(),
    listEmotionTasks: vi.fn(),
  }
})

const mockListDatasets = vi.mocked(client.listTrainingDatasets)
const mockCreateDataset = vi.mocked(client.createTrainingDataset)
const mockRenameDataset = vi.mocked(client.renameTrainingDataset)
const mockDeleteDataset = vi.mocked(client.deleteTrainingDataset)
const mockListItems = vi.mocked(client.listTrainingDatasetItems)
const mockListTasks = vi.mocked(client.listTranscriptTasks)
const mockListProviders = vi.mocked(client.listTrainingProviders)
const mockListModels = vi.mocked(client.listTrainingModels)
const mockListFeaturePoints = vi.mocked(client.listTrainingFeaturePoints)
const mockListEmotionTasks = vi.mocked(client.listEmotionTasks)

type Dataset = Awaited<ReturnType<typeof client.listTrainingDatasets>>[number]
type DatasetItem = Awaited<ReturnType<typeof client.listTrainingDatasetItems>>[number]

function apiError(status: number, serverMessage: string): Error {
  return Object.assign(new Error('API request failed'), { status, serverMessage })
}

const datasetA: Dataset = {
  id: 'd1',
  name: '歌回语料',
  type: 'audio',
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
}

const datasetB: Dataset = { ...datasetA, id: 'd2', name: '杂谈语料' }

const createdDataset: Dataset = { ...datasetA, id: 'd3', name: '新数据集' }

const item: DatasetItem = {
  id: 'i1',
  datasetId: 'd1',
  audioType: 'transcript',
  source: 't1',
  audioUri: 'b/live/101/recordings/a.flv',
  audioKey: 'transcript:t1:0',
  startMs: 0,
  endMs: 60000,
  text: '第一句',
  extensions: null,
  emotion: null,
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
}

async function pickOption(ariaLabel: string, optionText: string) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: ariaLabel }))
  await waitFor(() => {
    expect(document.querySelectorAll('.ant-select-item-option').length).toBeGreaterThan(0)
  })
  const option = Array.from(document.querySelectorAll('.ant-select-item-option')).find((el) =>
    (el.getAttribute('title') ?? '').includes(optionText),
  )
  if (!option) throw new Error(`option not found: ${optionText}`)
  fireEvent.click(option)
}

describe('DatasetsPage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListDatasets.mockResolvedValue([datasetA, datasetB])
    mockListItems.mockResolvedValue([])
    mockCreateDataset.mockResolvedValue(createdDataset)
    mockRenameDataset.mockResolvedValue({ ...datasetA, name: '歌回语料·修订' })
    mockDeleteDataset.mockResolvedValue(undefined)
    mockListTasks.mockResolvedValue([])
    mockListProviders.mockResolvedValue([])
    mockListModels.mockResolvedValue([])
    mockListFeaturePoints.mockResolvedValue([])
    mockListEmotionTasks.mockResolvedValue({
      items: [],
      counts: { pending: 0, running: 0, done: 0, failed: 0 },
    })
  })

  it('should render the 音频数据集 and 模型设置 tabs side by side', async () => {
    render(<DatasetsPage />)

    expect(screen.getByRole('tablist')).toBeInTheDocument()
    const tabs = screen.getAllByRole('tab')
    expect(tabs).toHaveLength(2)
    expect(tabs[0]).toHaveTextContent('音频数据集')
    expect(tabs[1]).toHaveTextContent('模型设置')
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true')
    expect(tabs[1]).toHaveAttribute('aria-selected', 'false')

    await screen.findByRole('combobox', { name: '数据集' })
  })

  it('should switch to 模型设置 and hide the dataset block, then switch back', async () => {
    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('tab', { name: '模型设置' }))

    expect(screen.getByRole('tab', { name: '模型设置' })).toHaveAttribute('aria-selected', 'true')
    expect(await screen.findByText('请先添加 Provider 并获取/添加模型')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Providers' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Models' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: '音频数据集' }))
    expect(await screen.findByRole('combobox', { name: '数据集' })).toBeVisible()
    expect(screen.getByRole('button', { name: /从 Transcript 导入/ })).toBeInTheDocument()
  })

  it('should list the datasets in the dropdown and switch the selection', async () => {
    render(<DatasetsPage />)

    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledWith('d1')
    })

    await pickOption('数据集', '杂谈语料')

    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledWith('d2')
    })
  })

  it('should create a dataset from the name alone and select it', async () => {
    mockListDatasets
      .mockResolvedValueOnce([datasetA, datasetB])
      .mockResolvedValue([datasetA, datasetB, createdDataset])

    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: '新建数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), { target: { value: '新数据集' } })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    await waitFor(() => {
      expect(mockCreateDataset).toHaveBeenCalledWith({ name: '新数据集', type: 'audio' })
    })
    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledWith('d3')
    })
  })

  it('should rename the selected dataset', async () => {
    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: '重命名数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), { target: { value: '歌回语料·修订' } })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    await waitFor(() => {
      expect(mockRenameDataset).toHaveBeenCalledWith('d1', { name: '歌回语料·修订' })
    })
  })

  it('should delete the selected dataset after a confirm that warns about the cascade', async () => {
    mockListDatasets.mockResolvedValueOnce([datasetA, datasetB]).mockResolvedValue([])

    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: '删除数据集' }))

    expect(await screen.findByText(/不可恢复/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '确认删除' }))

    await waitFor(() => {
      expect(mockDeleteDataset).toHaveBeenCalledWith('d1')
    })
  })

  it('should not delete the dataset when the confirm is rejected', async () => {
    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: '删除数据集' }))
    fireEvent.click(await screen.findByRole('button', { name: '取消' }))

    expect(mockDeleteDataset).not.toHaveBeenCalled()
  })

  it('should show a duplicate-name hint and keep the dataset list unchanged on create', async () => {
    mockCreateDataset.mockRejectedValue(apiError(409, '{"error":"dataset name exists"}'))

    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: '新建数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), { target: { value: '歌回语料' } })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    expect(await screen.findByText('数据集名字已存在')).toBeInTheDocument()
    // 未选中项变化（仍为 d1）：导入入口仍在
    expect(screen.getByRole('button', { name: /从 Transcript 导入/ })).toBeInTheDocument()
  })

  it('should show a duplicate-name hint when renaming to an existing name', async () => {
    mockRenameDataset.mockRejectedValue(apiError(409, '{"error":"dataset name exists"}'))

    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: '重命名数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), { target: { value: '杂谈语料' } })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    expect(await screen.findByText('数据集名字已存在')).toBeInTheDocument()
    expect(mockRenameDataset).toHaveBeenCalledWith('d1', { name: '杂谈语料' })
  })

  it('should hide the import entry while no dataset is selected', async () => {
    mockListDatasets.mockResolvedValue([])

    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    expect(screen.queryByRole('button', { name: /从 Transcript 导入/ })).not.toBeInTheDocument()
    expect(mockListItems).not.toHaveBeenCalled()
  })

  it('should open the import dialog as a modal above the page, listing only DONE transcripts', async () => {
    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: /从 Transcript 导入/ }))

    const dialog = await screen.findByRole('dialog', { name: '从 Transcript 导入' })
    expect(dialog).toBeInTheDocument()
    expect(mockListTasks).toHaveBeenCalledWith({ status: 'DONE' })
  })

  it('should close the import dialog when cancelled', async () => {
    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    fireEvent.click(screen.getByRole('button', { name: /从 Transcript 导入/ }))
    await screen.findByRole('dialog', { name: '从 Transcript 导入' })

    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: '从 Transcript 导入' })).not.toBeInTheDocument()
    })
  })

  it('should guide the user to import when the selected dataset has no items', async () => {
    render(<DatasetsPage />)

    expect(await screen.findByText('暂无音频数据，可从 Transcript 导入')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /从 Transcript 导入/ })).toBeInTheDocument()
  })

  it('should always render the download link and disable it while no dataset is selected', async () => {
    mockListDatasets.mockResolvedValue([])

    render(<DatasetsPage />)
    await screen.findByRole('combobox', { name: '数据集' })

    const link = screen.getByText('下载数据集').closest('a')
    expect(link).not.toBeNull()
    expect(link).toHaveAttribute('aria-disabled', 'true')
    // 禁用态不设置 href，点击不发起下载
    expect(link).not.toHaveAttribute('href')
    // 「从 Transcript 导入」未选中数据集仍不显示（既有行为不回退）
    expect(screen.queryByRole('button', { name: /从 Transcript 导入/ })).not.toBeInTheDocument()
  })

  it('should disable the download link when the selected dataset is empty', async () => {
    render(<DatasetsPage />)
    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledWith('d1')
    })

    const link = screen.getByText('下载数据集').closest('a')
    expect(link).toHaveAttribute('aria-disabled', 'true')
  })

  it('should enable the download link with the export href when the dataset has items', async () => {
    mockListItems.mockResolvedValue([item])

    render(<DatasetsPage />)

    await waitFor(() => {
      expect(screen.getByText('下载数据集').closest('a')).not.toHaveAttribute('aria-disabled')
    })
    const link = screen.getByText('下载数据集').closest('a')
    expect(link).toHaveAttribute('href', '/api/training/datasets/d1/export')
    expect(link).toHaveAttribute('download')
  })

  it('should disable the download link when the item list fails to load', async () => {
    mockListItems.mockImplementation((datasetId: string) =>
      datasetId === 'd1'
        ? Promise.resolve([item])
        : Promise.reject(apiError(500, 'boom')),
    )

    render(<DatasetsPage />)

    // d1 有条目 → 按钮可用
    await waitFor(() => {
      expect(screen.getByText('下载数据集').closest('a')).not.toHaveAttribute('aria-disabled')
    })

    // 切到 d2 且列表加载失败 → 按钮必须禁用（未知 = 禁用，不残留 d1 计数）
    await pickOption('数据集', '杂谈语料')

    await waitFor(() => {
      expect(screen.getByText('下载数据集').closest('a')).toHaveAttribute('aria-disabled', 'true')
    })
  })

  it('should not keep the previous item count in the window after switching datasets', async () => {
    let resolveD2: ((value: DatasetItem[]) => void) | undefined
    mockListItems.mockImplementation((datasetId: string) =>
      datasetId === 'd1'
        ? Promise.resolve([item])
        : new Promise<DatasetItem[]>((resolve) => {
            resolveD2 = resolve
          }),
    )

    render(<DatasetsPage />)

    await waitFor(() => {
      expect(screen.getByText('下载数据集').closest('a')).not.toHaveAttribute('aria-disabled')
    })

    await pickOption('数据集', '杂谈语料')

    // d2 仍在加载：计数已随选择清零 → 禁用，不残留 d1 的计数
    await waitFor(() => {
      expect(screen.getByText('下载数据集').closest('a')).toHaveAttribute('aria-disabled', 'true')
    })

    resolveD2?.([])
  })
})
