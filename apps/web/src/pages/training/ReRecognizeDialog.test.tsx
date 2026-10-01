import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { ReRecognizeDialog } from './ReRecognizeDialog'
import * as client from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingModel = components['schemas']['TrainingModel']
type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingModels: vi.fn(),
    resolveFeaturePointModel: vi.fn(),
    recognizeTrainingDatasetItem: vi.fn(),
    updateTrainingDatasetItem: vi.fn(),
  }
})

const mockListModels = vi.mocked(client.listTrainingModels)
const mockResolve = vi.mocked(client.resolveFeaturePointModel)
const mockRecognize = vi.mocked(client.recognizeTrainingDatasetItem)
const mockUpdateItem = vi.mocked(client.updateTrainingDatasetItem)

const model: TrainingModel = {
  id: 'm1',
  providerId: 'p1',
  providerName: 'openai-prod',
  model: 'gpt-4o',
  slug: 'openai-prod/gpt-4o',
  enabled: true,
  inputCapabilities: ['audio', 'text'],
  outputCapabilities: ['text'],
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
}

const updatedItem = {
  id: 'i1',
  datasetId: 'd1',
  audioType: 'transcript',
  source: 't1',
  audioUri: 'b/live/101/recordings/a.flv',
  audioKey: 'transcript:t1:0',
  startMs: 0,
  endMs: 60000,
  text: '识别后的文本',
  extensions: null,
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
} as TrainingDatasetItem

function apiError(status: number, serverMessage: string): Error {
  return Object.assign(new Error('API request failed'), { status, serverMessage })
}

function renderDialog() {
  const props = {
    datasetId: 'd1',
    item: { id: 'i1', text: '旧文本' },
    onClose: vi.fn(),
    onSaved: vi.fn(),
  }
  render(<ReRecognizeDialog {...props} />)
  return props
}

describe('ReRecognizeDialog', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListModels.mockResolvedValue([model])
    // 缺省无解析结果，避免影响既有用例的「手动选择」路径。
    mockResolve.mockResolvedValue({
      slug: 'dataset-clean',
      modelId: null,
      modelSlug: null,
      source: 'none',
    })
    mockRecognize.mockResolvedValue({ text: '识别后的文本' })
    mockUpdateItem.mockResolvedValue(updatedItem)
  })

  it('should render as a centred modal overlay', async () => {
    renderDialog()

    const dialog = await screen.findByRole('dialog', { name: '重新识别' })
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    const overlay = dialog.parentElement as HTMLElement
    expect(overlay.style.position).toBe('fixed')
    expect(overlay.style.backgroundColor).toContain('rgba(0, 0, 0, 0.5)')
    expect(overlay.style.display).toBe('flex')
    expect(overlay.style.justifyContent).toBe('center')
  })

  it('should list the enabled models by slug and filter candidates by feature point capability', async () => {
    renderDialog()

    await waitFor(() => {
      expect(mockListModels).toHaveBeenCalledWith(true, 'audio,text', 'text')
    })
    // 加载即完成 dataset-clean 功能点自动登记与解析
    expect(mockResolve).toHaveBeenCalledWith('dataset-clean', {
      name: '数据清洗',
      inputCapabilities: ['audio', 'text'],
      outputCapabilities: ['text'],
    })
    const select = screen.getByLabelText('识别模型')
    expect(screen.getByRole('option', { name: 'openai-prod/gpt-4o' })).toBeInTheDocument()
    fireEvent.change(select, { target: { value: 'm1' } })
    expect(select).toHaveValue('m1')
  })

  it('should default-select the resolved model and still allow a temporary override', async () => {
    mockResolve.mockResolvedValue({
      slug: 'dataset-clean',
      modelId: 'm1',
      modelSlug: 'openai-prod/gpt-4o',
      source: 'specified',
    })
    renderDialog()

    const select = await screen.findByLabelText('识别模型')
    await waitFor(() => {
      expect(select).toHaveValue('m1')
    })

    // 允许临时改选（解析结果只是默认，不强制锁定）
    fireEvent.change(select, { target: { value: '' } })
    expect(select).toHaveValue('')
  })

  it('should hint to configure models and capabilities first and disable recognize when there are none', async () => {
    mockListModels.mockResolvedValue([])

    renderDialog()

    expect(await screen.findByText('请先在模型设置中配置模型与能力')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '识别' })).toBeDisabled()
  })

  it('should recognize once while loading and refill the text on success', async () => {
    let resolveRecognize: (value: { text: string }) => void = () => {}
    mockRecognize.mockReturnValue(
      new Promise((resolve) => {
        resolveRecognize = resolve
      }),
    )
    const props = renderDialog()

    fireEvent.change(await screen.findByLabelText('识别模型'), { target: { value: 'm1' } })
    const recognizeButton = screen.getByRole('button', { name: '识别' })
    fireEvent.click(recognizeButton)
    fireEvent.click(recognizeButton)

    expect(mockRecognize).toHaveBeenCalledTimes(1)
    expect(recognizeButton).toBeDisabled()

    resolveRecognize({ text: '识别后的文本' })

    await waitFor(() => {
      expect(screen.getByLabelText('文本')).toHaveValue('识别后的文本')
    })
    expect(props.onClose).not.toHaveBeenCalled()
  })

  it('should keep the original text when recognition fails', async () => {
    mockRecognize.mockRejectedValue(
      apiError(400, '{"error":"model does not support audio input"}'),
    )
    renderDialog()

    fireEvent.change(await screen.findByLabelText('识别模型'), { target: { value: 'm1' } })
    fireEvent.click(screen.getByRole('button', { name: '识别' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('该模型不支持音频输入')
    expect(screen.getByLabelText('文本')).toHaveValue('旧文本')
  })

  it('should save through updateTrainingDatasetItem and call onSaved', async () => {
    const props = renderDialog()

    const textarea = await screen.findByLabelText('文本')
    fireEvent.change(textarea, { target: { value: '手工修订' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => {
      expect(mockUpdateItem).toHaveBeenCalledWith('d1', 'i1', { text: '手工修订' })
    })
    await waitFor(() => {
      expect(props.onSaved).toHaveBeenCalledWith(updatedItem)
    })
    expect(mockRecognize).not.toHaveBeenCalled()
  })

  it('should make no request when cancelled', async () => {
    const props = renderDialog()

    await screen.findByLabelText('识别模型')
    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    expect(props.onClose).toHaveBeenCalledTimes(1)
    expect(mockRecognize).not.toHaveBeenCalled()
    expect(mockUpdateItem).not.toHaveBeenCalled()
  })
})
