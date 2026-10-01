import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { EmotionSubmitDialog } from './EmotionSubmitDialog'
import * as client from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingModel = components['schemas']['TrainingModel']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingModels: vi.fn(),
    resolveFeaturePointModel: vi.fn(),
    createEmotionTasks: vi.fn(),
  }
})

const mockListModels = vi.mocked(client.listTrainingModels)
const mockResolve = vi.mocked(client.resolveFeaturePointModel)
const mockCreateTasks = vi.mocked(client.createEmotionTasks)

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

function apiError(status: number, serverMessage: string): Error {
  return Object.assign(new Error('API request failed'), { status, serverMessage })
}

async function pickSelect(ariaLabel: string, optionText: string) {
  const combo = screen.getByRole('combobox', { name: ariaLabel }) as HTMLElement
  fireEvent.mouseDown(combo)
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
    itemIds: ['i1', 'i2'],
    onClose: vi.fn(),
    onSubmitted: vi.fn(),
  }
  render(<EmotionSubmitDialog {...props} />)
  return props
}

describe('EmotionSubmitDialog', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListModels.mockResolvedValue([model])
    mockResolve.mockResolvedValue({
      slug: 'emotion-from-audio',
      modelId: null,
      modelSlug: null,
      source: 'none',
    })
    mockCreateTasks.mockResolvedValue({ created: 2, skipped: 0 })
  })

  it('should require a model before submitting', async () => {
    renderDialog()

    await waitFor(() => {
      expect(mockListModels).toHaveBeenCalledWith(true, 'audio,text', 'text')
    })
    // 加载即完成 emotion-from-audio 功能点自动登记与解析
    expect(mockResolve).toHaveBeenCalledWith('emotion-from-audio', {
      name: '输入语音识别情绪',
      inputCapabilities: ['audio', 'text'],
      outputCapabilities: ['text'],
    })

    fireEvent.mouseDown(screen.getByRole('combobox', { name: '情绪识别模型' }))
    expect(await screen.findByTitle('openai-prod/gpt-4o')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '提交' })).toBeDisabled()

    await pickSelect('情绪识别模型', 'openai-prod/gpt-4o')
    expect(screen.getByRole('button', { name: '提交' })).not.toBeDisabled()
  })

  it('should default-select the resolved model', async () => {
    mockResolve.mockResolvedValue({
      slug: 'emotion-from-audio',
      modelId: 'm1',
      modelSlug: 'openai-prod/gpt-4o',
      source: 'default',
    })
    renderDialog()

    // antd Select 关闭态显示所选 slug：解析结果被默认选中，无需手动选择即可提交。
    await screen.findByText('openai-prod/gpt-4o')
    await waitFor(() => {
      expect(screen.getByRole('button', { name: '提交' })).toBeEnabled()
    })
    fireEvent.click(screen.getByRole('button', { name: '提交' }))
    await waitFor(() => {
      expect(mockCreateTasks).toHaveBeenCalledWith('d1', ['i1', 'i2'], 'm1')
    })
  })

  it('should guide to configure models and capabilities when there is no candidate', async () => {
    mockListModels.mockResolvedValue([])

    renderDialog()

    expect(await screen.findByText('请先在模型设置中配置模型与能力')).toBeInTheDocument()
  })

  it('should submit the selected items and report created and skipped counts', async () => {
    mockCreateTasks.mockResolvedValue({ created: 2, skipped: 1 })
    const props = renderDialog()

    await pickSelect('情绪识别模型', 'openai-prod/gpt-4o')
    fireEvent.click(screen.getByRole('button', { name: '提交' }))

    await waitFor(() => {
      expect(mockCreateTasks).toHaveBeenCalledWith('d1', ['i1', 'i2'], 'm1')
    })
    expect(await screen.findByText('已提交 2 条，跳过 1 条')).toBeInTheDocument()
    expect(props.onSubmitted).toHaveBeenCalledWith({ created: 2, skipped: 1 })
  })

  it('should show a readable error when the submission fails', async () => {
    mockCreateTasks.mockRejectedValue(apiError(404, '{"error":"model not found"}'))
    renderDialog()

    await pickSelect('情绪识别模型', 'openai-prod/gpt-4o')
    fireEvent.click(screen.getByRole('button', { name: '提交' }))

    expect(await screen.findByText('模型不存在')).toBeInTheDocument()
  })

  it('should make no request when cancelled', async () => {
    const props = renderDialog()

    await screen.findByRole('combobox', { name: '情绪识别模型' })
    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    expect(props.onClose).toHaveBeenCalledTimes(1)
    expect(mockCreateTasks).not.toHaveBeenCalled()
  })
})
