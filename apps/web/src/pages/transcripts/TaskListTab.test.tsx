import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, fireEvent } from '@testing-library/react'
import { TaskListTab } from './TaskListTab'
import * as client from '../../api/client'

class MockEventSource {
  static instances: MockEventSource[] = []
  url: string
  closed = false
  private listeners: Record<string, Array<(event: { data?: string }) => void>> = {}

  constructor(url: string) {
    this.url = url
    MockEventSource.instances.push(this)
  }

  addEventListener(type: string, listener: (event: { data?: string }) => void) {
    this.listeners[type] = this.listeners[type] ?? []
    this.listeners[type].push(listener)
  }

  removeEventListener(type: string, listener: (event: { data?: string }) => void) {
    this.listeners[type] = (this.listeners[type] ?? []).filter((item) => item !== listener)
  }

  close() {
    this.closed = true
  }

  emit(type: string, payload?: unknown) {
    for (const listener of this.listeners[type] ?? []) {
      listener({ data: payload === undefined ? undefined : JSON.stringify(payload) })
    }
  }
}

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTranscriptTasks: vi.fn(),
    getTranscriptTask: vi.fn(),
  }
})

const mockListTasks = vi.mocked(client.listTranscriptTasks)
const mockGetTask = vi.mocked(client.getTranscriptTask)

describe('TaskListTab', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    MockEventSource.instances = []
    vi.stubGlobal('EventSource', MockEventSource)
    mockListTasks.mockResolvedValue([])
    mockGetTask.mockResolvedValue({ task: {}, segments: [] })
  })

  it('should render task list with in-progress tasks first', async () => {
    mockListTasks.mockResolvedValue([
      { id: 'a', status: 'DONE', title: '已完成场次', modelName: '模型A', position: 100, createdAt: '2026-09-01T00:00:00Z' },
      { id: 'b', status: 'RUNNING', title: '识别中场次', modelName: '模型B', position: 200, createdAt: '2026-09-02T00:00:00Z' },
      { id: 'c', status: 'PENDING', title: '等待中场次', modelName: '模型C', position: 0, createdAt: '2026-09-03T00:00:00Z' },
      { id: 'd', status: 'FAILED', title: '失败场次', modelName: '模型D', position: 0, createdAt: '2026-09-04T00:00:00Z' },
    ])

    render(<TaskListTab />)

    await waitFor(() => {
      expect(screen.getByText('识别中场次')).toBeInTheDocument()
    })

    const rows = screen.getAllByRole('row')
    expect(rows[1]).toHaveTextContent('识别中')
    expect(rows[2]).toHaveTextContent('等待中')
    expect(screen.getByText('模型B')).toBeInTheDocument()
  })

  it('should append live segments, dedupe and refill on reconnect replay', async () => {
    mockListTasks.mockResolvedValue([
      { id: 't1', status: 'RUNNING', title: '场次1', modelName: '默认模型', position: 1000, createdAt: '2026-09-02T00:00:00Z' },
    ])
    mockGetTask.mockResolvedValue({
      task: { id: 't1' },
      segments: [
        { start: 0, end: 1000, text: '第一句' },
        { start: 1000, end: 2000, text: '第二句' },
      ],
    })

    render(<TaskListTab />)

    fireEvent.click(await screen.findByText('查看'))

    await waitFor(() => {
      expect(screen.getByText('第一句')).toBeInTheDocument()
    })

    expect(MockEventSource.instances).toHaveLength(1)
    const source = MockEventSource.instances[0]
    expect(source.url).toBe('/api/transcripts/task/t1/sse')

    source.emit('message', { start: 2000, end: 3000, text: '第三句' })
    await waitFor(() => {
      expect(screen.getByText('第三句')).toBeInTheDocument()
    })

    source.emit('message', { start: 0, end: 1000, text: '第一句' })
    source.emit('message', { start: 1000, end: 2000, text: '第二句' })
    source.emit('message', { start: 2000, end: 3000, text: '第三句' })
    source.emit('message', { start: 3000, end: 4000, text: '第四句' })

    await waitFor(() => {
      expect(screen.getByText('第四句')).toBeInTheDocument()
    })

    expect(screen.getAllByText('第一句')).toHaveLength(1)
    expect(screen.getAllByRole('listitem')).toHaveLength(4)
  })

  it('should close the EventSource on unmount', async () => {
    mockListTasks.mockResolvedValue([
      { id: 't1', status: 'RUNNING', title: '场次1', createdAt: '2026-09-02T00:00:00Z' },
    ])
    mockGetTask.mockResolvedValue({ task: { id: 't1' }, segments: [] })

    const { unmount } = render(<TaskListTab />)

    fireEvent.click(await screen.findByText('查看'))
    await waitFor(() => {
      expect(MockEventSource.instances).toHaveLength(1)
    })

    const source = MockEventSource.instances[0]
    unmount()

    expect(source.closed).toBe(true)
  })

  it('should render empty state when there are no tasks', async () => {
    mockListTasks.mockResolvedValue([])

    render(<TaskListTab />)

    await waitFor(() => {
      expect(screen.getByText('暂无转写任务')).toBeInTheDocument()
    })
  })
})
