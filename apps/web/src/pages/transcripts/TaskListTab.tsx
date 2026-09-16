import { useCallback, useEffect, useState } from 'react'
import {
  listTranscriptTasks,
  getTranscriptTask,
  openTranscriptStream,
} from '../../api/client'
import type { components as transcriptComponents } from '../../api/schema-recording-transcript'

type TranscriptTask = transcriptComponents['schemas']['TranscriptTask']
type TranscriptSegment = transcriptComponents['schemas']['TranscriptSegment']

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

export function TaskListTab() {
  const [tasks, setTasks] = useState<TranscriptTask[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null)
  const [segments, setSegments] = useState<TranscriptSegment[]>([])
  const [segmentsLoading, setSegmentsLoading] = useState(false)
  const [segmentsError, setSegmentsError] = useState<string | null>(null)

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
    if (!selectedTaskId) {
      return
    }

    let cancelled = false
    setSegments([])
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
  }, [selectedTaskId])

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
                      onClick={() => setSelectedTaskId(task.id ?? null)}
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
          <h3>识别结果</h3>
          {segmentsLoading && segments.length === 0 ? (
            <p>加载中...</p>
          ) : segmentsError && segments.length === 0 ? (
            <p style={{ color: '#f87171' }}>{segmentsError}</p>
          ) : segments.length === 0 ? (
            <p>暂无识别结果</p>
          ) : (
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
          )}
        </div>
      )}
    </div>
  )
}
