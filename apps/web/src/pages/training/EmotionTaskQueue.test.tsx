import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react'
import { EmotionTaskQueue, EMOTION_TASK_POLL_INTERVAL_MS } from './EmotionTaskQueue'
import * as client from '../../api/client'
import { formatDateTime } from '../../utils/format'
import type { components } from '../../api/schema-training'

type TrainingEmotionTask = components['schemas']['TrainingEmotionTask']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listEmotionTasks: vi.fn(),
    retryEmotionTask: vi.fn(),
  }
})

const mockListTasks = vi.mocked(client.listEmotionTasks)
const mockRetry = vi.mocked(client.retryEmotionTask)

const failedTask: TrainingEmotionTask = {
  id: 't1',
  datasetId: 'd1',
  itemId: 'i1',
  audioKey: 'transcript:t1:0',
  modelId: 'm1',
  modelSlug: 'openai-prod/gpt-4o',
  status: 'failed',
  attempts: 2,
  error: '模型服务不可用',
  emotion: null,
  createdAt: '2026-09-27T12:30:00Z',
  startedAt: '2026-09-27T12:31:00Z',
  finishedAt: '2026-09-27T12:32:00Z',
}

const doneTask: TrainingEmotionTask = {
  ...failedTask,
  id: 't2',
  itemId: 'i2',
  audioKey: 'transcript:t2:0',
  status: 'done',
  attempts: 1,
  error: null,
}

const counts = { pending: 1, running: 0, done: 1, failed: 1 }

describe('EmotionTaskQueue', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListTasks.mockResolvedValue({ items: [failedTask, doneTask], counts })
    mockRetry.mockResolvedValue({ ...failedTask, status: 'pending', error: null })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('should render the status counts, the task table and a retry button only on failed rows', async () => {
    render(<EmotionTaskQueue datasetId="d1" />)

    expect(await screen.findByText('待处理 1')).toBeInTheDocument()
    expect(screen.getByText('执行中 0')).toBeInTheDocument()
    expect(screen.getByText('已完成 1')).toBeInTheDocument()
    expect(screen.getByText('失败 1')).toBeInTheDocument()

    for (const header of ['条目', '模型', '状态', '次数', '错误', '时间']) {
      expect(screen.getByRole('columnheader', { name: header })).toBeInTheDocument()
    }

    expect(screen.getByText('transcript:t1:0')).toBeInTheDocument()
    expect(screen.getAllByText('openai-prod/gpt-4o').length).toBeGreaterThan(0)
    expect(screen.getByText('模型服务不可用')).toBeInTheDocument()
    expect(screen.getAllByText(formatDateTime(failedTask.createdAt)).length).toBeGreaterThan(0)
    expect(screen.getByText('失败')).toBeInTheDocument()
    expect(screen.getByText('已完成')).toBeInTheDocument()

    expect(screen.getByRole('button', { name: '重试 t1' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '重试 t2' })).not.toBeInTheDocument()
  })

  it('should request the queue filtered by datasetId', async () => {
    render(<EmotionTaskQueue datasetId="d1" />)

    await waitFor(() => {
      expect(mockListTasks).toHaveBeenCalledWith({ datasetId: 'd1' })
    })
  })

  it('should render the empty state when there are no tasks', async () => {
    mockListTasks.mockResolvedValue({
      items: [],
      counts: { pending: 0, running: 0, done: 0, failed: 0 },
    })

    render(<EmotionTaskQueue datasetId="d1" />)

    expect(await screen.findByText('暂无情绪识别任务')).toBeInTheDocument()
  })

  it('should retry a failed task and reload the queue', async () => {
    render(<EmotionTaskQueue datasetId="d1" />)
    await screen.findByText('transcript:t1:0')

    fireEvent.click(screen.getByRole('button', { name: '重试 t1' }))

    await waitFor(() => {
      expect(mockRetry).toHaveBeenCalledWith('t1')
    })
    await waitFor(() => {
      expect(mockListTasks).toHaveBeenCalledTimes(2)
    })
  })

  it('should ignore repeated retry clicks for a task while the request is in flight', async () => {
    let resolveRetry: (value: TrainingEmotionTask) => void = () => {}
    mockRetry.mockReturnValue(
      new Promise((resolve) => {
        resolveRetry = resolve
      }),
    )

    render(<EmotionTaskQueue datasetId="d1" />)
    await screen.findByText('transcript:t1:0')

    const retryButton = screen.getByRole('button', { name: '重试 t1' })
    fireEvent.click(retryButton)
    fireEvent.click(retryButton)

    expect(mockRetry).toHaveBeenCalledTimes(1)
    expect(retryButton).toBeDisabled()

    resolveRetry({ ...failedTask, status: 'pending', error: null })
    await waitFor(() => {
      expect(mockListTasks).toHaveBeenCalledTimes(2)
    })
  })

  it('should poll every 10 seconds and clear the interval on unmount', async () => {
    vi.useFakeTimers()
    const { unmount } = render(<EmotionTaskQueue datasetId="d1" />)

    await act(async () => {
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(mockListTasks).toHaveBeenCalledTimes(1)

    await act(async () => {
      vi.advanceTimersByTime(EMOTION_TASK_POLL_INTERVAL_MS)
    })
    expect(mockListTasks).toHaveBeenCalledTimes(2)

    unmount()
    await act(async () => {
      vi.advanceTimersByTime(EMOTION_TASK_POLL_INTERVAL_MS * 3)
    })
    expect(mockListTasks).toHaveBeenCalledTimes(2)
  })
})
