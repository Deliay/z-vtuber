import { useCallback, useEffect, useRef, useState } from 'react'
import { apiErrorCodeText, apiErrorMessage, listEmotionTasks, retryEmotionTask } from '../../api/client'
import { formatDateTime } from '../../utils/format'
import type { components } from '../../api/schema-training'

type TrainingEmotionTask = components['schemas']['TrainingEmotionTask']

export interface EmotionTaskQueueProps {
  /** 按数据集过滤（缺省即全局队列）。 */
  datasetId?: string
  /** 外部刷新令牌（如提交新任务后自增）。 */
  reloadToken?: number
}

const STATUS_LABELS: Record<string, string> = {
  pending: '待处理',
  running: '执行中',
  done: '已完成',
  failed: '失败',
}

const EMPTY_COUNTS = { pending: 0, running: 0, done: 0, failed: 0 }

const cellStyle = { textAlign: 'left' as const, padding: '0.5rem' }

/** 轮询间隔（毫秒）。 */
export const EMOTION_TASK_POLL_INTERVAL_MS = 10000

/**
 * 情绪识别任务队列区块：状态统计 chips + 任务表格 + FAILED 行「重试」；
 * 每 10s 轮询刷新，组件卸载时清理定时器。
 */
export function EmotionTaskQueue({ datasetId, reloadToken = 0 }: EmotionTaskQueueProps) {
  const [tasks, setTasks] = useState<TrainingEmotionTask[]>([])
  const [counts, setCounts] = useState(EMPTY_COUNTS)
  const [error, setError] = useState('')
  /** 正在重试的任务 id（按任务禁用「重试」防重复提交）。 */
  const [retryingIds, setRetryingIds] = useState<string[]>([])
  /** 请求序号：仅接受最新一次 `load` 的响应，避免慢响应后到覆盖新数据（如重试后显示旧状态）。 */
  const loadSeq = useRef(0)

  const load = useCallback(async () => {
    const seq = ++loadSeq.current
    try {
      const data = await listEmotionTasks({ datasetId })
      if (seq !== loadSeq.current) return
      setTasks(data.items ?? [])
      setCounts(data.counts ?? EMPTY_COUNTS)
      setError('')
    } catch (err) {
      if (seq !== loadSeq.current) return
      setError(apiErrorMessage(err, '加载任务队列失败'))
    }
  }, [datasetId])

  useEffect(() => {
    void load()
  }, [load, reloadToken])

  useEffect(() => {
    const timer = setInterval(() => {
      void load()
    }, EMOTION_TASK_POLL_INTERVAL_MS)
    return () => clearInterval(timer)
  }, [load])

  const handleRetry = async (task: TrainingEmotionTask) => {
    if (retryingIds.includes(task.id)) return
    setRetryingIds((prev) => [...prev, task.id])
    try {
      await retryEmotionTask(task.id)
      await load()
    } catch (err) {
      setError(apiErrorMessage(err, '重试任务失败'))
    } finally {
      setRetryingIds((prev) => prev.filter((id) => id !== task.id))
    }
  }

  return (
    <div className="emotion-task-queue">
      <h3>情绪任务队列</h3>

      <div style={{ display: 'flex', gap: '0.75rem', marginBottom: '0.75rem' }}>
        <span>待处理 {counts.pending}</span>
        <span>执行中 {counts.running}</span>
        <span>已完成 {counts.done}</span>
        <span>失败 {counts.failed}</span>
      </div>

      {error && <div role="alert">{error}</div>}

      {tasks.length === 0 ? (
        <div>暂无情绪识别任务</div>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={cellStyle}>条目</th>
              <th style={cellStyle}>模型</th>
              <th style={cellStyle}>状态</th>
              <th style={cellStyle}>次数</th>
              <th style={cellStyle}>错误</th>
              <th style={cellStyle}>时间</th>
              <th style={cellStyle}>操作</th>
            </tr>
          </thead>
          <tbody>
            {tasks.map((task) => (
              <tr key={task.id}>
                <td style={cellStyle}>{task.audioKey || task.itemId}</td>
                <td style={cellStyle}>{task.modelSlug}</td>
                <td style={cellStyle}>{STATUS_LABELS[task.status] ?? task.status}</td>
                <td style={cellStyle}>{task.attempts}</td>
                <td style={cellStyle}>{task.error ? apiErrorCodeText(task.error) : '-'}</td>
                <td style={cellStyle}>{formatDateTime(task.createdAt)}</td>
                <td style={cellStyle}>
                  {task.status === 'failed' && (
                    <button
                      aria-label={`重试 ${task.id}`}
                      onClick={() => void handleRetry(task)}
                      disabled={retryingIds.includes(task.id)}
                    >
                      重试
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
