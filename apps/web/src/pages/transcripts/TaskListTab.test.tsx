import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, fireEvent, within, act } from '@testing-library/react'
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

class MockAudio extends EventTarget {
  static instances: MockAudio[] = []
  src = ''
  paused = true
  currentTime = 0
  preload = ''
  play = vi.fn((): Promise<void> => {
    this.paused = false
    return Promise.resolve()
  })
  pause = vi.fn((): void => {
    this.paused = true
  })
  removeAttribute = vi.fn((name: string): void => {
    if (name === 'src') {
      this.src = ''
    }
  })

  constructor() {
    super()
    MockAudio.instances.push(this)
  }
}

type TaskItem = Awaited<ReturnType<typeof client.listTranscriptTasks>>[number]
type SegmentItem = Awaited<ReturnType<typeof client.getTranscriptTask>>['segments'][number]
type TaskDetail = Awaited<ReturnType<typeof client.getTranscriptTask>>

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTranscriptTasks: vi.fn(),
    getTranscriptTask: vi.fn(),
    listTranscriptRecordings: vi.fn(),
    listTranscriptModels: vi.fn(),
    getRoomTranscripts: vi.fn(),
    enqueueTranscript: vi.fn(),
    retryTranscriptTask: vi.fn(),
    updateTranscriptTaskOffset: vi.fn(),
  }
})

const mockListTasks = vi.mocked(client.listTranscriptTasks)
const mockGetTask = vi.mocked(client.getTranscriptTask)
const mockListRecordings = vi.mocked(client.listTranscriptRecordings)
const mockListModels = vi.mocked(client.listTranscriptModels)
const mockGetRoomTranscripts = vi.mocked(client.getRoomTranscripts)
const mockEnqueue = vi.mocked(client.enqueueTranscript)
const mockRetry = vi.mocked(client.retryTranscriptTask)
const mockUpdateOffset = vi.mocked(client.updateTranscriptTaskOffset)

const panelTask: TaskItem = {
  offsetMs: 0,
  id: 't1',
  status: 'DONE',
  title: '场次1',
  roomId: 101,
  file: '[RAW] a.flv',
  createdAt: '2026-09-02T00:00:00Z',
}

const panelSegments: SegmentItem[] = [
  { start: 0, end: 1000, text: '第一句' },
  { start: 1000, end: 2500, text: '第二句' },
]

async function getAudio(): Promise<MockAudio> {
  await waitFor(() => {
    expect(MockAudio.instances.length).toBeGreaterThan(0)
  })
  return MockAudio.instances[0]
}

function emitAudioEvent(audio: MockAudio, type: string): void {
  act(() => {
    audio.dispatchEvent(new Event(type))
  })
}

async function openTaskPanel(listTasks: TaskItem[], detail: TaskDetail) {
  mockListTasks.mockResolvedValue(listTasks)
  mockGetTask.mockResolvedValue(detail)
  const view = render(<TaskListTab />)
  fireEvent.click(await screen.findByText('查看'))
  if (detail.segments.length > 0) {
    await screen.findByText(detail.segments[0].text)
  } else {
    await screen.findByText('暂无识别结果')
  }
  return view
}

describe('TaskListTab', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    MockEventSource.instances = []
    MockAudio.instances = []
    vi.stubGlobal('EventSource', MockEventSource)
    vi.stubGlobal('Audio', MockAudio)
    mockListTasks.mockResolvedValue([])
    mockGetTask.mockResolvedValue({ task: { offsetMs: 0 }, segments: [] })
    mockListRecordings.mockResolvedValue([
      { id: 'r1', roomId: 101, mid: 1, title: '晚间歌回', files: [], startedAt: '2026-09-16T12:00:00Z' },
    ])
    mockListModels.mockResolvedValue([
      {
        id: 'm1',
        name: '默认模型',
        provider: 'ali-fun-asr',
        type: 'streaming',
        model: 'fun-asr-realtime',
        enabled: true,
        maxConcurrency: 1,
      },
    ])
    mockGetRoomTranscripts.mockResolvedValue({
      roomId: 101,
      autoEnqueueEnabled: true,
      items: [
        {
          recordingId: 'r1',
          title: '晚间歌回',
          startedAt: '2026-09-16T12:00:00Z',
          endedAt: null,
          tasks: [
            {
              taskId: 't1',
              modelId: 'm1',
              modelName: '默认模型',
              status: 'FAILED',
              position: 0,
              source: 'manual',
              updatedAt: '2026-09-16T13:00:00Z',
            },
          ],
        },
      ],
    })
    mockEnqueue.mockResolvedValue({ taskId: 't2', status: 'PENDING' })
    mockRetry.mockResolvedValue({ id: 't1', status: 'RUNNING', offsetMs: 0 })
    mockUpdateOffset.mockResolvedValue({ id: 't1', offsetMs: 0 })
  })

  it('should render task list with in-progress tasks first', async () => {
    mockListTasks.mockResolvedValue([
      { id: 'a', status: 'DONE', title: '已完成场次', modelName: '模型A', position: 100, createdAt: '2026-09-01T00:00:00Z', offsetMs: 0 },
      { id: 'b', status: 'RUNNING', title: '识别中场次', modelName: '模型B', position: 200, createdAt: '2026-09-02T00:00:00Z', offsetMs: 0 },
      { id: 'c', status: 'PENDING', title: '等待中场次', modelName: '模型C', position: 0, createdAt: '2026-09-03T00:00:00Z', offsetMs: 0 },
      { id: 'd', status: 'FAILED', title: '失败场次', modelName: '模型D', position: 0, createdAt: '2026-09-04T00:00:00Z', offsetMs: 0 },
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
      { id: 't1', status: 'RUNNING', title: '场次1', modelName: '默认模型', position: 1000, createdAt: '2026-09-02T00:00:00Z', offsetMs: 0 },
    ])
    mockGetTask.mockResolvedValue({
      task: { id: 't1', offsetMs: 0 },
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

  it('should re-fetch the selected task when the refresh button is clicked', async () => {
    mockListTasks.mockResolvedValue([
      { id: 't1', status: 'RUNNING', title: '场次1', createdAt: '2026-09-02T00:00:00Z', offsetMs: 0 },
    ])
    mockGetTask.mockResolvedValue({
      task: { id: 't1', offsetMs: 0 },
      segments: [{ start: 0, end: 1000, text: '第一句' }],
    })

    render(<TaskListTab />)

    fireEvent.click(await screen.findByText('查看'))

    await waitFor(() => {
      expect(mockGetTask).toHaveBeenCalledTimes(1)
    })

    fireEvent.click(screen.getByRole('button', { name: '刷新任务-t1' }))

    await waitFor(() => {
      expect(mockGetTask).toHaveBeenCalledTimes(2)
    })
    expect(mockGetTask).toHaveBeenLastCalledWith('t1')
  })

  it('should close the EventSource on unmount', async () => {
    mockListTasks.mockResolvedValue([
      { id: 't1', status: 'RUNNING', title: '场次1', createdAt: '2026-09-02T00:00:00Z', offsetMs: 0 },
    ])
    mockGetTask.mockResolvedValue({ task: { id: 't1', offsetMs: 0 }, segments: [] })

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

  it('should enqueue a manual task and refresh the task list', async () => {
    mockListTasks
      .mockResolvedValueOnce([])
      .mockResolvedValue([
        { id: 't2', status: 'PENDING', title: '新任务', modelName: '默认模型', createdAt: '2026-09-16T00:00:00Z', offsetMs: 0 },
      ])

    render(<TaskListTab />)

    await screen.findByText('暂无转写任务')

    fireEvent.change(screen.getByLabelText('录播场次'), { target: { value: 'r1' } })
    fireEvent.change(screen.getByLabelText('识别模型'), { target: { value: 'm1' } })
    fireEvent.click(screen.getByText('发起识别'))

    await waitFor(() => {
      expect(mockEnqueue).toHaveBeenCalledWith('r1', 'm1')
    })
    expect(await screen.findByText('新任务')).toBeInTheDocument()
    expect(mockListTasks).toHaveBeenCalledTimes(2)
  })

  it('should show the existing task id when a manual enqueue returns 409', async () => {
    mockEnqueue.mockRejectedValue(
      Object.assign(new Error('conflict'), {
        status: 409,
        serverMessage: JSON.stringify({ taskId: 't-existing', status: 'PENDING' }),
      }),
    )

    render(<TaskListTab />)

    await screen.findByText('发起识别')

    fireEvent.change(screen.getByLabelText('录播场次'), { target: { value: 'r1' } })
    fireEvent.change(screen.getByLabelText('识别模型'), { target: { value: 'm1' } })
    fireEvent.click(screen.getByText('发起识别'))

    expect(await screen.findByText('已有任务 t-existing')).toBeInTheDocument()
    expect(screen.queryByText('发起识别失败')).not.toBeInTheDocument()
  })

  it('should query a room transcripts and retry a FAILED task', async () => {
    render(<TaskListTab />)

    await screen.findByText('查询')

    fireEvent.change(screen.getByLabelText('主播'), { target: { value: '101' } })
    fireEvent.click(screen.getByText('查询'))

    await waitFor(() => {
      expect(mockGetRoomTranscripts).toHaveBeenCalledWith(101)
    })

    const retryButton = await screen.findByText('重试')
    fireEvent.click(retryButton)

    await waitFor(() => {
      expect(mockRetry).toHaveBeenCalledWith('t1')
    })
  })

  it('should render recording options with anchor, size and 进行中 fallback', async () => {
    mockListRecordings.mockResolvedValue([
      {
        id: 'r1',
        roomId: 101,
        mid: 1,
        anchorName: '主播A',
        title: '晚间歌回',
        files: [],
        size: 2048,
        startedAt: '2026-09-16T20:05:00',
        endedAt: '2026-09-16T22:30:00',
      },
      {
        id: 'r2',
        roomId: 202,
        mid: 2,
        anchorName: null,
        title: '杂谈',
        files: [],
        size: null,
        startedAt: '2026-09-17T09:00:00',
        endedAt: null,
      },
    ])

    render(<TaskListTab />)

    expect(
      await screen.findByRole('option', {
        name: '[主播A][2.0 KB] 晚间歌回 - 2026-09-16-20-05 - 2026-09-16-22-30',
      }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('option', {
        name: '[202][未知大小] 杂谈 - 2026-09-17-09-00 - 进行中',
      }),
    ).toBeInTheDocument()
  })

  it('should show anchorName in the room filter and fall back to roomId', async () => {
    mockListRecordings.mockResolvedValue([
      { id: 'r1', roomId: 101, mid: 1, anchorName: '主播A', title: '晚间歌回', files: [] },
      { id: 'r2', roomId: 202, mid: 2, title: '杂谈', files: [] },
    ])

    render(<TaskListTab />)

    expect(await screen.findByRole('option', { name: '主播A（101）' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: '202' })).toBeInTheDocument()
  })

  it('should collapse the detail and close the EventSource when clicking 收起', async () => {
    mockListTasks.mockResolvedValue([
      { id: 't1', status: 'RUNNING', title: '场次1', createdAt: '2026-09-02T00:00:00Z', offsetMs: 0 },
    ])
    mockGetTask.mockResolvedValue({
      task: { id: 't1', offsetMs: 0 },
      segments: [{ start: 0, end: 1000, text: '第一句' }],
    })

    render(<TaskListTab />)

    fireEvent.click(await screen.findByText('查看'))
    await waitFor(() => {
      expect(screen.getByText('第一句')).toBeInTheDocument()
    })

    const source = MockEventSource.instances[0]
    fireEvent.click(screen.getByRole('button', { name: '收起识别结果' }))

    await waitFor(() => {
      expect(screen.queryByText('第一句')).not.toBeInTheDocument()
    })
    expect(screen.queryByText('识别结果')).not.toBeInTheDocument()
    expect(source.closed).toBe(true)
  })

  it('should constrain and scroll the segments container', async () => {
    mockListTasks.mockResolvedValue([
      { id: 't1', status: 'RUNNING', title: '场次1', createdAt: '2026-09-02T00:00:00Z', offsetMs: 0 },
    ])
    mockGetTask.mockResolvedValue({
      task: { id: 't1', offsetMs: 0 },
      segments: [{ start: 0, end: 1000, text: '第一句' }],
    })

    render(<TaskListTab />)

    fireEvent.click(await screen.findByText('查看'))
    const segment = await screen.findByText('第一句')
    const container = segment.closest('ul')?.parentElement as HTMLElement | null

    expect(container).not.toBeNull()
    expect(container?.style.maxHeight).toBe('24rem')
    expect(container?.style.overflowY).toBe('auto')
  })

  it('should play a segment clip, switch source and stop on the second click', async () => {
    await openTaskPanel([panelTask], { task: panelTask, segments: panelSegments })

    const audio = await getAudio()
    const [first, second] = screen.getAllByRole('listitem')

    fireEvent.click(within(first).getByRole('button', { name: '播放' }))
    expect(audio.src).toBe(
      '/api/b/live/101/recordings/%5BRAW%5D%20a.flv/audio?startMs=0&endMs=1000',
    )
    expect(audio.play).toHaveBeenCalledTimes(1)

    fireEvent.click(within(second).getByRole('button', { name: '播放' }))
    expect(audio.src).toBe(
      '/api/b/live/101/recordings/%5BRAW%5D%20a.flv/audio?startMs=1000&endMs=2500',
    )
    expect(audio.play).toHaveBeenCalledTimes(2)
    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
    expect(within(second).getByRole('button', { name: '停止' })).toBeInTheDocument()

    fireEvent.click(within(second).getByRole('button', { name: '停止' }))
    expect(audio.pause).toHaveBeenCalled()
    expect(audio.src).toBe('')
  })

  it('should show 音频暂不可用 when the audio emits an error', async () => {
    await openTaskPanel([panelTask], { task: panelTask, segments: panelSegments })
    const audio = await getAudio()
    const [first] = screen.getAllByRole('listitem')

    fireEvent.click(within(first).getByRole('button', { name: '播放' }))
    emitAudioEvent(audio, 'error')

    expect(await screen.findByText('音频暂不可用')).toBeInTheDocument()
    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
  })

  it('should disable the play buttons when the task file is empty', async () => {
    const taskWithoutFile: TaskItem = { ...panelTask, file: null }
    await openTaskPanel([taskWithoutFile], { task: taskWithoutFile, segments: [panelSegments[0]] })

    const button = within(screen.getAllByRole('listitem')[0]).getByRole('button', {
      name: '播放',
    })
    expect(button).toBeDisabled()
    expect(button).toHaveAttribute('title', '音频暂不可用')
  })

  it('should pause and clear src when the selected task switches', async () => {
    const otherTask: TaskItem = {
      ...panelTask,
      id: 't2',
      title: '场次2',
      file: 'b.flv',
      createdAt: '2026-09-01T00:00:00Z',
    }
    mockListTasks.mockResolvedValue([panelTask, otherTask])
    mockGetTask.mockImplementation(async (taskId: string) => ({
      task: { id: taskId, offsetMs: 0 },
      segments: [{ start: 0, end: 1000, text: taskId === 't2' ? 'T2句子' : '第一句' }],
    }))

    render(<TaskListTab />)
    fireEvent.click(await screen.findByRole('button', { name: '查看任务-t1' }))
    await screen.findByText('第一句')

    const audio = await getAudio()
    const [first] = screen.getAllByRole('listitem')
    fireEvent.click(within(first).getByRole('button', { name: '播放' }))
    expect(audio.src).toContain('startMs=0&endMs=1000')

    fireEvent.click(screen.getByRole('button', { name: '查看任务-t2' }))

    await waitFor(() => {
      expect(audio.pause).toHaveBeenCalled()
    })
    expect(audio.src).toBe('')
  })

  it('should pause and clear src when the panel is collapsed', async () => {
    await openTaskPanel([panelTask], { task: panelTask, segments: panelSegments })
    const audio = await getAudio()
    const [first] = screen.getAllByRole('listitem')
    fireEvent.click(within(first).getByRole('button', { name: '播放' }))

    fireEvent.click(screen.getByRole('button', { name: '收起识别结果' }))

    await waitFor(() => {
      expect(audio.pause).toHaveBeenCalled()
    })
    expect(audio.src).toBe('')
    expect(screen.queryByText('识别结果')).not.toBeInTheDocument()
  })

  it('should pause and clear src when the tab unmounts', async () => {
    const { unmount } = await openTaskPanel([panelTask], {
      task: panelTask,
      segments: panelSegments,
    })
    const audio = await getAudio()
    const [first] = screen.getAllByRole('listitem')
    fireEvent.click(within(first).getByRole('button', { name: '播放' }))

    unmount()

    expect(audio.pause).toHaveBeenCalled()
    expect(audio.src).toBe('')
  })

  it('should render subtitle download links next to the collapse button', async () => {
    await openTaskPanel([panelTask], { task: panelTask, segments: [panelSegments[0]] })

    const srt = screen.getByRole('link', { name: '下载 SRT' })
    const ass = screen.getByRole('link', { name: '下载 ASS' })
    expect(srt).toHaveAttribute('href', '/api/transcripts/task/t1/subtitle?format=srt')
    expect(ass).toHaveAttribute('href', '/api/transcripts/task/t1/subtitle?format=ass')
    expect(srt).toHaveAttribute('download')
    expect(ass).toHaveAttribute('download')
    expect(srt).not.toHaveAttribute('aria-disabled')
    expect(ass).not.toHaveAttribute('aria-disabled')
  })

  it('should disable subtitle download links when there are no segments', async () => {
    await openTaskPanel([panelTask], { task: panelTask, segments: [] })

    const srt = screen.getByRole('link', { name: '下载 SRT' })
    const ass = screen.getByRole('link', { name: '下载 ASS' })
    expect(srt).toHaveAttribute('href', '/api/transcripts/task/t1/subtitle?format=srt')
    expect(ass).toHaveAttribute('href', '/api/transcripts/task/t1/subtitle?format=ass')
    expect(srt).toHaveAttribute('aria-disabled', 'true')
    expect(srt).toHaveAttribute('title', '暂无识别结果')
    expect(ass).toHaveAttribute('aria-disabled', 'true')
    expect(ass).toHaveAttribute('title', '暂无识别结果')
  })

  it('should show the playback offset entry with the current effective value', async () => {
    const offsetTask: TaskItem = { ...panelTask, offsetMs: 2500 }
    await openTaskPanel([offsetTask], { task: offsetTask, segments: panelSegments })

    expect(screen.getByText('播放偏移')).toBeInTheDocument()
    expect(screen.getByLabelText('播放偏移（秒）')).toHaveValue(2.5)
    expect(screen.getByRole('button', { name: '保存播放偏移' })).toBeInTheDocument()
  })

  it('should save 2.5s as offsetMs=2500, then refetch the detail and rebuild the stream', async () => {
    mockUpdateOffset.mockResolvedValue({ ...panelTask, offsetMs: 2500 })
    await openTaskPanel([panelTask], { task: panelTask, segments: panelSegments })
    expect(MockEventSource.instances).toHaveLength(1)

    fireEvent.change(screen.getByLabelText('播放偏移（秒）'), { target: { value: '2.5' } })
    fireEvent.click(screen.getByRole('button', { name: '保存播放偏移' }))

    await waitFor(() => {
      expect(mockUpdateOffset).toHaveBeenCalledWith('t1', 2500)
    })
    await waitFor(() => {
      expect(mockGetTask).toHaveBeenCalledTimes(2)
    })
    expect(MockEventSource.instances).toHaveLength(2)
    expect(MockEventSource.instances[0].closed).toBe(true)
    expect(screen.getByLabelText('播放偏移（秒）')).toHaveValue(2.5)
  })

  it('should save a negative offset as offsetMs=-1500', async () => {
    mockUpdateOffset.mockResolvedValue({ ...panelTask, offsetMs: -1500 })
    await openTaskPanel([panelTask], { task: panelTask, segments: panelSegments })

    fireEvent.change(screen.getByLabelText('播放偏移（秒）'), { target: { value: '-1.5' } })
    fireEvent.click(screen.getByRole('button', { name: '保存播放偏移' }))

    await waitFor(() => {
      expect(mockUpdateOffset).toHaveBeenCalledWith('t1', -1500)
    })
  })

  it('should reject an invalid offset without sending a request', async () => {
    await openTaskPanel([panelTask], { task: panelTask, segments: panelSegments })

    fireEvent.change(screen.getByLabelText('播放偏移（秒）'), { target: { value: '999999' } })
    fireEvent.click(screen.getByRole('button', { name: '保存播放偏移' }))

    expect(await screen.findByText('偏移无效')).toBeInTheDocument()
    expect(mockUpdateOffset).not.toHaveBeenCalled()

    fireEvent.change(screen.getByLabelText('播放偏移（秒）'), { target: { value: 'abc' } })
    fireEvent.click(screen.getByRole('button', { name: '保存播放偏移' }))

    expect(await screen.findByText('偏移无效')).toBeInTheDocument()
    expect(mockUpdateOffset).not.toHaveBeenCalled()
  })

  it('should keep the current value and show 偏移无效 when the server rejects with 400', async () => {
    mockUpdateOffset.mockRejectedValue(
      Object.assign(new Error('bad request'), {
        status: 400,
        serverMessage: '{"error":"offsetMs out of range"}',
      }),
    )
    const offsetTask: TaskItem = { ...panelTask, offsetMs: 2500 }
    await openTaskPanel([offsetTask], { task: offsetTask, segments: panelSegments })

    fireEvent.change(screen.getByLabelText('播放偏移（秒）'), { target: { value: '3' } })
    fireEvent.click(screen.getByRole('button', { name: '保存播放偏移' }))

    expect(await screen.findByText('偏移无效')).toBeInTheDocument()
    expect(screen.getByLabelText('播放偏移（秒）')).toHaveValue(2.5)
    expect(mockGetTask).toHaveBeenCalledTimes(1)
    expect(MockEventSource.instances).toHaveLength(1)
  })
})
