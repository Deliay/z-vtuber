import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
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
    recognizeTrainingDatasetItem: vi.fn(),
    updateTrainingDatasetItem: vi.fn(),
  }
})

const mockListModels = vi.mocked(client.listTrainingModels)
const mockRecognize = vi.mocked(client.recognizeTrainingDatasetItem)
const mockUpdateItem = vi.mocked(client.updateTrainingDatasetItem)

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

async function pickSelect(optionText: string) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: '识别模型' }))
  await waitFor(() => {
    expect(document.querySelectorAll('.ant-select-item-option').length).toBeGreaterThan(0)
  })
  const option = Array.from(document.querySelectorAll('.ant-select-item-option')).find((el) =>
    (el.getAttribute('title') ?? '').includes(optionText),
  )
  if (!option) throw new Error(`option not found: ${optionText}`)
  fireEvent.click(option)
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
    mockRecognize.mockResolvedValue({ text: '识别后的文本' })
    mockUpdateItem.mockResolvedValue(updatedItem)
  })

  it('should render as a centred modal', async () => {
    renderDialog()

    const dialog = await screen.findByRole('dialog', { name: '重新识别' })
    expect(dialog).toBeInTheDocument()
  })

  it('should list the enabled models by slug', async () => {
    renderDialog()

    await waitFor(() => {
      expect(mockListModels).toHaveBeenCalledWith(true)
    })
    fireEvent.mouseDown(screen.getByRole('combobox', { name: '识别模型' }))
    expect(await screen.findByTitle('openai-prod/gpt-4o')).toBeInTheDocument()
  })

  it('should hint to configure models first and disable recognize when there are none', async () => {
    mockListModels.mockResolvedValue([])

    renderDialog()

    expect(await screen.findByText('请先在模型设置中配置')).toBeInTheDocument()
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

    await pickSelect('openai-prod/gpt-4o')
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

    await pickSelect('openai-prod/gpt-4o')
    fireEvent.click(screen.getByRole('button', { name: '识别' }))

    expect(await screen.findByText('该模型不支持音频输入')).toBeInTheDocument()
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

    await screen.findByRole('combobox', { name: '识别模型' })
    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    expect(props.onClose).toHaveBeenCalledTimes(1)
    expect(mockRecognize).not.toHaveBeenCalled()
    expect(mockUpdateItem).not.toHaveBeenCalled()
  })
})
