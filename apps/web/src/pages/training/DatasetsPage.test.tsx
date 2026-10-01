import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
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

    await screen.findByLabelText('数据集')
  })

  it('should switch to 模型设置 and hide the dataset block, then switch back', async () => {
    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('tab', { name: '模型设置' }))

    expect(screen.getByRole('tab', { name: '模型设置' })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    expect(await screen.findByText('请先添加 Provider 并获取/添加模型')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Providers' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Models' })).toBeInTheDocument()
    // 模型设置 Tab 不渲染数据集选择器/导入入口/条目列表
    expect(screen.queryByLabelText('数据集')).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: '从 Transcript 导入' }),
    ).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: '音频数据集' }))
    expect(await screen.findByLabelText('数据集')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '从 Transcript 导入' })).toBeInTheDocument()
  })

  it('should list the datasets in the dropdown and switch the selection', async () => {
    render(<DatasetsPage />)

    const select = await screen.findByLabelText('数据集')
    expect(within(select).getByRole('option', { name: '歌回语料' })).toBeInTheDocument()
    expect(within(select).getByRole('option', { name: '杂谈语料' })).toBeInTheDocument()
    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledWith('d1')
    })

    fireEvent.change(select, { target: { value: 'd2' } })

    await waitFor(() => {
      expect(mockListItems).toHaveBeenCalledWith('d2')
    })
  })

  it('should create a dataset from the name alone and select it', async () => {
    mockListDatasets
      .mockResolvedValueOnce([datasetA, datasetB])
      .mockResolvedValue([datasetA, datasetB, createdDataset])

    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '新建数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), {
      target: { value: '新数据集' },
    })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    await waitFor(() => {
      expect(mockCreateDataset).toHaveBeenCalledWith({ name: '新数据集', type: 'audio' })
    })
    await waitFor(() => {
      expect(screen.getByLabelText('数据集')).toHaveValue('d3')
    })
    expect(screen.getByRole('button', { name: '从 Transcript 导入' })).toBeInTheDocument()
  })

  it('should rename the selected dataset', async () => {
    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '重命名数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), {
      target: { value: '歌回语料·修订' },
    })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    await waitFor(() => {
      expect(mockRenameDataset).toHaveBeenCalledWith('d1', { name: '歌回语料·修订' })
    })
  })

  it('should delete the selected dataset after a confirm that warns about the cascade', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    mockListDatasets.mockResolvedValueOnce([datasetA, datasetB]).mockResolvedValue([])

    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '删除数据集' }))

    expect(confirmSpy).toHaveBeenCalledWith(expect.stringContaining('不可恢复'))
    await waitFor(() => {
      expect(mockDeleteDataset).toHaveBeenCalledWith('d1')
    })
    await waitFor(() => {
      expect(
        screen.queryByRole('button', { name: '从 Transcript 导入' }),
      ).not.toBeInTheDocument()
    })
  })

  it('should not delete the dataset when the confirm is rejected', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false)

    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '删除数据集' }))

    expect(mockDeleteDataset).not.toHaveBeenCalled()
  })

  it('should show a duplicate-name hint and keep the dataset list unchanged on create', async () => {
    mockCreateDataset.mockRejectedValue(
      apiError(409, '{"error":"dataset name exists"}'),
    )

    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '新建数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), {
      target: { value: '歌回语料' },
    })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    expect(await screen.findByText('数据集名字已存在')).toBeInTheDocument()
    expect(screen.getAllByRole('option', { name: '歌回语料' })).toHaveLength(1)
    expect(screen.getByLabelText('数据集')).toHaveValue('d1')
  })

  it('should show a duplicate-name hint when renaming to an existing name', async () => {
    mockRenameDataset.mockRejectedValue(
      apiError(409, '{"error":"dataset name exists"}'),
    )

    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '重命名数据集' }))
    fireEvent.change(screen.getByLabelText('数据集名字'), {
      target: { value: '杂谈语料' },
    })
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    expect(await screen.findByText('数据集名字已存在')).toBeInTheDocument()
    expect(
      within(screen.getByLabelText('数据集')).getByRole('option', { name: '杂谈语料' }),
    ).toBeInTheDocument()
  })

  it('should hide the import entry while no dataset is selected', async () => {
    mockListDatasets.mockResolvedValue([])

    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    expect(
      screen.queryByRole('button', { name: '从 Transcript 导入' }),
    ).not.toBeInTheDocument()
    expect(mockListItems).not.toHaveBeenCalled()
  })

  it('should open the import dialog as a modal above the page, listing only DONE transcripts', async () => {
    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '从 Transcript 导入' }))

    const dialog = await screen.findByRole('dialog', { name: '从 Transcript 导入' })
    expect(dialog).toBeInTheDocument()
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    // 模态遮罩：全屏 fixed + 半透明遮罩 + 居中容器（按结构样式契约断言）
    const overlay = dialog.parentElement as HTMLElement
    expect(overlay.style.position).toBe('fixed')
    expect(overlay.style.backgroundColor).toContain('rgba(0, 0, 0, 0.5)')
    expect(overlay.style.justifyContent).toBe('center')
    expect(mockListTasks).toHaveBeenCalledWith({ status: 'DONE' })
  })

  it('should close the import dialog by clicking the overlay', async () => {
    render(<DatasetsPage />)
    await screen.findByLabelText('数据集')

    fireEvent.click(screen.getByRole('button', { name: '从 Transcript 导入' }))
    const dialog = await screen.findByRole('dialog', { name: '从 Transcript 导入' })

    fireEvent.click(dialog.parentElement as HTMLElement)

    await waitFor(() => {
      expect(
        screen.queryByRole('dialog', { name: '从 Transcript 导入' }),
      ).not.toBeInTheDocument()
    })
  })

  it('should guide the user to import when the selected dataset has no items', async () => {
    render(<DatasetsPage />)

    expect(
      await screen.findByText('暂无音频数据，可从 Transcript 导入'),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '从 Transcript 导入' })).toBeInTheDocument()
  })
})
