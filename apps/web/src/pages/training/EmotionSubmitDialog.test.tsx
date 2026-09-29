import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { EmotionSubmitDialog } from './EmotionSubmitDialog'
import * as client from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingModel = components['schemas']['TrainingModel']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingModels: vi.fn(),
    createEmotionTasks: vi.fn(),
  }
})

const mockListModels = vi.mocked(client.listTrainingModels)
const mockCreateTasks = vi.mocked(client.createEmotionTasks)

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

function apiError(status: number, serverMessage: string): Error {
  return Object.assign(new Error('API request failed'), { status, serverMessage })
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
    mockCreateTasks.mockResolvedValue({ created: 2, skipped: 0 })
  })

  it('should require a model before submitting', async () => {
    renderDialog()

    await waitFor(() => {
      expect(mockListModels).toHaveBeenCalledWith(true)
    })
    expect(screen.getByRole('option', { name: 'openai-prod/gpt-4o' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '提交' })).toBeDisabled()

    fireEvent.change(screen.getByLabelText('情绪识别模型'), { target: { value: 'm1' } })
    expect(screen.getByRole('button', { name: '提交' })).not.toBeDisabled()
  })

  it('should submit the selected items and report created and skipped counts', async () => {
    mockCreateTasks.mockResolvedValue({ created: 2, skipped: 1 })
    const props = renderDialog()

    fireEvent.change(await screen.findByLabelText('情绪识别模型'), { target: { value: 'm1' } })
    fireEvent.click(screen.getByRole('button', { name: '提交' }))

    await waitFor(() => {
      expect(mockCreateTasks).toHaveBeenCalledWith('d1', ['i1', 'i2'], 'm1')
    })
    expect(await screen.findByRole('status')).toHaveTextContent('已提交 2 条，跳过 1 条')
    expect(props.onSubmitted).toHaveBeenCalledWith({ created: 2, skipped: 1 })
  })

  it('should show a readable error when the submission fails', async () => {
    mockCreateTasks.mockRejectedValue(apiError(404, '{"error":"model not found"}'))
    renderDialog()

    fireEvent.change(await screen.findByLabelText('情绪识别模型'), { target: { value: 'm1' } })
    fireEvent.click(screen.getByRole('button', { name: '提交' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('模型不存在')
  })

  it('should make no request when cancelled', async () => {
    const props = renderDialog()

    await screen.findByLabelText('情绪识别模型')
    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    expect(props.onClose).toHaveBeenCalledTimes(1)
    expect(mockCreateTasks).not.toHaveBeenCalled()
  })
})
