import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  listTranscriptTasks,
  getTranscriptTask,
  openTranscriptStream,
  listTranscriptRecordings,
  listTranscriptModels,
  getRoomTranscripts,
  enqueueTranscript,
  retryTranscriptTask,
} from '../../api/client'
import type { components as transcriptComponents } from '../../api/schema-recording-transcript'
import { formatBytes, formatDateTime } from '../../utils/format'

type TranscriptTask = transcriptComponents['schemas']['TranscriptTask']
type TranscriptSegment = transcriptComponents['schemas']['TranscriptSegment']
type TranscriptRecordingItem = transcriptComponents['schemas']['RecordingItem']
type TranscriptModel = transcriptComponents['schemas']['TranscriptModel']
type RoomTranscriptView = transcriptComponents['schemas']['RoomTranscriptView']

const STATUS_LABELS: Record<string, string> = {
  PENDING: '等待中',
  RUNNING: '识别中',
  DONE: '已完成',
  FAILED: '失败',
}

const STATUS_COLORS: Record<string, string> = {
  PENDING: '#eab308',
  RUNNING: '#22c55e',
  DONE: '#38bdf8',
  FAILED: '#f87171',
}

const STATUS_ORDER: Record<string, number> = {
  RUNNING: 0,
  PENDING: 1,
  FAILED: 2,
  DONE: 3,
}

const inputStyle: React.CSSProperties = {
  padding: '0.5rem',
  backgroundColor: '#334155',
  color: '#e2e8f0',
  border: '1px solid #475569',
  borderRadius: '0.25rem',
}

function sortTasks(tasks: TranscriptTask[]): TranscriptTask[] {
  return [...tasks].sort((a, b) => {
    const orderA = STATUS_ORDER[a.status ?? ''] ?? 4
    const orderB = STATUS_ORDER[b.status ?? ''] ?? 4
    if (orderA !== orderB) {
      return orderA - orderB
    }
    return (b.createdAt ?? '').localeCompare(a.createdAt ?? '')
  })
}

function mergeSegments(
  prev: TranscriptSegment[],
  incoming: TranscriptSegment[],
): TranscriptSegment[] {
  const byStart = new Map<number, TranscriptSegment>()
  for (const segment of prev) {
    byStart.set(segment.start, segment)
  }
  for (const segment of incoming) {
    byStart.set(segment.start, segment)
  }
  return [...byStart.values()].sort((a, b) => a.start - b.start)
}

function formatPosition(position?: number | null): string {
  if (position === undefined || position === null) {
    return '-'
  }
  const totalSeconds = Math.floor(position / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${String(seconds).padStart(2, '0')}`
}

function parseExistingTaskId(error: unknown): string | undefined {
  const apiError = error as { status?: number; serverMessage?: string }
  if (apiError.status !== 409) {
    return undefined
  }
  try {
    const body = JSON.parse(apiError.serverMessage ?? '') as { taskId?: string }
    return body.taskId
  } catch {
    return undefined
  }
}

export function TaskListTab() {
  const [tasks, setTasks] = useState<TranscriptTask[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [recordings, setRecordings] = useState<TranscriptRecordingItem[]>([])
  const [models, setModels] = useState<TranscriptModel[]>([])

  const [enqueueRecordingId, setEnqueueRecordingId] = useState('')
  const [enqueueModelId, setEnqueueModelId] = useState('')
  const [enqueueMessage, setEnqueueMessage] = useState<string | null>(null)
  const [enqueueError, setEnqueueError] = useState<string | null>(null)

  const [selectedRoomId, setSelectedRoomId] = useState('')
  const [roomView, setRoomView] = useState<RoomTranscriptView | null>(null)
  const [roomError, setRoomError] = useState<string | null>(null)

  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)
  const loadedTaskIdRef = useRef<string | null>(null)
  const [segments, setSegments] = useState<TranscriptSegment[]>([])
  const [segmentsLoading, setSegmentsLoading] = useState(false)
  const [segmentsError, setSegmentsError] = useState<string | null>(null)
  const segmentsContainerRef = useRef<HTMLDivElement | null>(null)

  const fetchTasks = useCallback(async () => {
    try {
      setError(null)
      setLoading(true)
      const data = await listTranscriptTasks()
      setTasks(sortTasks(data))
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchTasks()
  }, [fetchTasks])

  useEffect(() => {
    let cancelled = false
    Promise.all([listTranscriptRecordings(), listTranscriptModels()])
      .then(([recordingsData, modelsData]) => {
        if (cancelled) {
          return
        }
        setRecordings(recordingsData)
        setModels(modelsData)
      })
      .catch(() => {
        // Reference data is optional; manual enqueue/room view degrade gracefully.
      })
    return () => {
      cancelled = true
    }
  }, [])

  const enabledModels = useMemo(
    () => models.filter((model) => model.enabled !== false),
    [models],
  )

  const roomOptions = useMemo(() => {
    const options: { roomId: number; label: string }[] = []
    const seen = new Set<number>()
    for (const recording of recordings) {
      if (typeof recording.roomId === 'number' && !seen.has(recording.roomId)) {
        seen.add(recording.roomId)
        const anchorName = recording.anchorName?.trim()
        options.push({
          roomId: recording.roomId,
          label: anchorName
            ? `${anchorName}（${recording.roomId}）`
            : String(recording.roomId),
        })
      }
    }
    return options
  }, [recordings])

  useEffect(() => {
    if (!selectedTaskId) {
      return
    }

    let cancelled = false
    if (loadedTaskIdRef.current !== selectedTaskId) {
      setSegments([])
      loadedTaskIdRef.current = selectedTaskId
    }
    setSegmentsError(null)
    setSegmentsLoading(true)

    getTranscriptTask(selectedTaskId)
      .then((detail) => {
        if (cancelled) {
          return
        }
        setSegments((prev) => mergeSegments(prev, detail.segments ?? []))
      })
      .catch(() => {
        if (!cancelled) {
          setSegmentsError('识别结果加载失败')
        }
      })
      .finally(() => {
        if (!cancelled) {
          setSegmentsLoading(false)
        }
      })

    const source = openTranscriptStream(selectedTaskId)
    const handleMessage = (event: MessageEvent) => {
      try {
        const segment = JSON.parse(event.data) as TranscriptSegment
        setSegments((prev) => mergeSegments(prev, [segment]))
      } catch {
        // Ignore malformed payloads
      }
    }
    const handleDone = () => {
      source.close()
    }

    source.addEventListener('message', handleMessage)
    source.addEventListener('done', handleDone)

    return () => {
      cancelled = true
      source.removeEventListener('message', handleMessage)
      source.removeEventListener('done', handleDone)
      source.close()
    }
  }, [selectedTaskId, refreshKey])

  useEffect(() => {
    const container = segmentsContainerRef.current
    if (!container) {
      return
    }
    if (container.scrollHeight - container.scrollTop - container.clientHeight < 40) {
      container.scrollTop = container.scrollHeight
    }
  }, [segments])

  const handleTaskAction = (taskId: string | undefined) => {
    if (!taskId) {
      return
    }
    if (selectedTaskId === taskId) {
      setRefreshKey((key) => key + 1)
    } else {
      setSelectedTaskId(taskId)
    }
  }

  const handleEnqueue = async (e: React.FormEvent) => {
    e.preventDefault()
    setEnqueueMessage(null)
    setEnqueueError(null)

    if (!enqueueRecordingId || !enqueueModelId) {
      setEnqueueError('请选择录播场次与模型')
      return
    }

    try {
      const result = await enqueueTranscript(enqueueRecordingId, enqueueModelId)
      setEnqueueMessage(`已创建任务 ${result.taskId}`)
      await fetchTasks()
      if (selectedRoomId) {
        const view = await getRoomTranscripts(Number(selectedRoomId))
        setRoomView(view)
      }
    } catch (err) {
      const existingTaskId = parseExistingTaskId(err)
      if (existingTaskId) {
        setEnqueueMessage(`已有任务 ${existingTaskId}`)
        return
      }
      setEnqueueError('发起识别失败')
    }
  }

  const handleQueryRoom = async () => {
    if (!selectedRoomId) {
      setRoomError('请选择主播')
      return
    }
    try {
      setRoomError(null)
      const view = await getRoomTranscripts(Number(selectedRoomId))
      setRoomView(view)
    } catch {
      setRoomError('场次任务加载失败')
    }
  }

  const handleRetry = async (taskId: string) => {
    try {
      await retryTranscriptTask(taskId)
      if (selectedRoomId) {
        const view = await getRoomTranscripts(Number(selectedRoomId))
        setRoomView(view)
      }
    } catch {
      setRoomError('重试失败')
    }
  }

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '1rem' }}>
        <h3 style={{ margin: 0 }}>任务列表</h3>
        <button onClick={fetchTasks} style={{ padding: '0.25rem 0.75rem' }}>
          刷新
        </button>
      </div>

      {loading && tasks.length === 0 ? (
        <p>加载中...</p>
      ) : error && tasks.length === 0 ? (
        <p style={{ color: '#f87171' }}>{error}</p>
      ) : tasks.length === 0 ? (
        <p>暂无转写任务</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>状态</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>场次</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>模型</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>进度</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
            </tr>
          </thead>
          <tbody>
            {tasks.map((task) => {
              const status = task.status ?? 'PENDING'
              return (
                <tr key={task.id}>
                  <td style={{ padding: '0.5rem' }}>
                    <span style={{ color: STATUS_COLORS[status] ?? '#e2e8f0' }}>
                      {STATUS_LABELS[status] ?? status}
                    </span>
                  </td>
                  <td style={{ padding: '0.5rem' }}>{task.title ?? task.recordingId ?? '-'}</td>
                  <td style={{ padding: '0.5rem' }}>{task.modelName ?? task.modelId ?? '-'}</td>
                  <td style={{ padding: '0.5rem' }}>{formatPosition(task.position)}</td>
                  <td style={{ padding: '0.5rem' }}>
                    <button
                      onClick={() => handleTaskAction(task.id)}
                      aria-label={
                        selectedTaskId === task.id
                          ? `刷新任务-${task.id ?? ''}`
                          : `查看任务-${task.id ?? ''}`
                      }
                      style={{ padding: '0.25rem 0.5rem' }}
                    >
                      {selectedTaskId === task.id ? '刷新' : '查看'}
                    </button>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}

      {error && !loading && tasks.length > 0 && (
        <p style={{ color: '#f87171', fontSize: '0.875rem' }}>任务刷新失败，展示的是上次结果</p>
      )}

      {selectedTaskId && (
        <div style={{ marginTop: '1.5rem' }}>
          <div
            style={{
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center',
            }}
          >
            <h3 style={{ margin: '0 0 0.5rem' }}>识别结果</h3>
            <button
              onClick={() => setSelectedTaskId(null)}
              aria-label="收起识别结果"
              style={{ padding: '0.25rem 0.75rem' }}
            >
              收起
            </button>
          </div>
          {segmentsLoading && segments.length === 0 ? (
            <p>加载中...</p>
          ) : segmentsError && segments.length === 0 ? (
            <p style={{ color: '#f87171' }}>{segmentsError}</p>
          ) : segments.length === 0 ? (
            <p>暂无识别结果</p>
          ) : (
            <div ref={segmentsContainerRef} style={{ maxHeight: '24rem', overflowY: 'auto' }}>
              <ul style={{ margin: 0, paddingLeft: '1rem' }}>
                {segments.map((segment) => (
                  <li key={segment.start}>
                    <span style={{ color: '#94a3b8', marginRight: '0.5rem' }}>
                      {formatPosition(segment.start)}
                    </span>
                    <span>{segment.text}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}

      <section style={{ marginTop: '2rem' }}>
        <h3>手动发起识别</h3>
        <form
          onSubmit={handleEnqueue}
          style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', alignItems: 'center' }}
        >
          <select
            aria-label="录播场次"
            value={enqueueRecordingId}
            onChange={(e) => setEnqueueRecordingId(e.target.value)}
            style={inputStyle}
          >
            <option value="">选择录播场次</option>
            {recordings.map((recording) => {
              const startTime = recording.startedAt
                ? formatDateTime(recording.startedAt)
                : '-'
              const endTime = recording.endedAt
                ? formatDateTime(recording.endedAt)
                : '进行中'
              return (
                <option key={recording.id} value={recording.id}>
                  {`[${recording.anchorName ?? recording.roomId}][${formatBytes(recording.size)}] ${
                    recording.title ?? recording.id
                  } - ${startTime} - ${endTime}`}
                </option>
              )
            })}
          </select>

          <select
            aria-label="识别模型"
            value={enqueueModelId}
            onChange={(e) => setEnqueueModelId(e.target.value)}
            style={inputStyle}
          >
            <option value="">选择模型</option>
            {enabledModels.map((model) => (
              <option key={model.id} value={model.id}>
                {model.name}
              </option>
            ))}
          </select>

          <button type="submit" style={{ padding: '0.5rem 1rem' }}>
            发起识别
          </button>
        </form>
        {enqueueMessage && <p style={{ color: '#22c55e' }}>{enqueueMessage}</p>}
        {enqueueError && <p style={{ color: '#f87171' }}>{enqueueError}</p>}
      </section>

      <section style={{ marginTop: '2rem' }}>
        <h3>场次任务</h3>
        <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', marginBottom: '0.5rem' }}>
          <select
            aria-label="主播"
            value={selectedRoomId}
            onChange={(e) => setSelectedRoomId(e.target.value)}
            style={inputStyle}
          >
            <option value="">选择主播</option>
            {roomOptions.map((option) => (
              <option key={option.roomId} value={String(option.roomId)}>
                {option.label}
              </option>
            ))}
          </select>
          <button onClick={handleQueryRoom} style={{ padding: '0.5rem 1rem' }}>
            查询
          </button>
        </div>
        {roomError && <p style={{ color: '#f87171' }}>{roomError}</p>}
        {roomView && (
          <div>
            <p style={{ color: '#94a3b8' }}>
              自动识别：{roomView.autoEnqueueEnabled ? '已开启' : '已关闭'}
            </p>
            {roomView.items.length === 0 ? (
              <p>暂无场次</p>
            ) : (
              roomView.items.map((item) => (
                <div
                  key={item.recordingId}
                  style={{ border: '1px solid #334155', borderRadius: '0.25rem', padding: '0.5rem', marginBottom: '0.5rem' }}
                >
                  <div>{item.title ?? item.recordingId}</div>
                  {item.tasks.length === 0 ? (
                    <p style={{ margin: 0, color: '#94a3b8' }}>暂无任务</p>
                  ) : (
                    <ul style={{ margin: 0, paddingLeft: '1rem' }}>
                      {item.tasks.map((task) => (
                        <li key={task.taskId}>
                          {task.modelName ?? task.modelId} -{' '}
                          {STATUS_LABELS[task.status ?? ''] ?? task.status} -{' '}
                          {formatPosition(task.position)}
                          {task.status === 'FAILED' && (
                            <button
                              onClick={() => handleRetry(task.taskId ?? '')}
                              style={{ marginLeft: '0.5rem', padding: '0.25rem 0.5rem' }}
                            >
                              重试
                            </button>
                          )}
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ))
            )}
          </div>
        )}
      </section>
    </div>
  )
}
