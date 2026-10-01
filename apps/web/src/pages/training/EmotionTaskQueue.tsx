import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Button, Empty, Space, Table, Tag, Typography } from 'antd'
import type { TableColumnsType } from 'antd'
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

const STATUS_TAGS: Record<string, string> = {
  pending: 'gold',
  running: 'blue',
  done: 'green',
  failed: 'red',
}

const EMPTY_COUNTS = { pending: 0, running: 0, done: 0, failed: 0 }

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

  const columns: TableColumnsType<TrainingEmotionTask> = [
    { title: '条目', key: 'audioKey', render: (_v, task) => task.audioKey || task.itemId },
    { title: '模型', dataIndex: 'modelSlug' },
    {
      title: '状态',
      key: 'status',
      width: 100,
      render: (_v, task) => (
        <Tag color={STATUS_TAGS[task.status] ?? 'default'}>
          {STATUS_LABELS[task.status] ?? task.status}
        </Tag>
      ),
    },
    { title: '次数', dataIndex: 'attempts', width: 80 },
    {
      title: '错误',
      key: 'error',
      render: (_v, task) => (task.error ? apiErrorCodeText(task.error) : '-'),
    },
    { title: '时间', key: 'createdAt', render: (_v, task) => formatDateTime(task.createdAt) },
    {
      title: '操作',
      key: 'action',
      width: 100,
      render: (_v, task) =>
        task.status === 'failed' ? (
          <Button
            size="small"
            aria-label={`重试 ${task.id}`}
            loading={retryingIds.includes(task.id)}
            disabled={retryingIds.includes(task.id)}
            onClick={() => void handleRetry(task)}
          >
            重试
          </Button>
        ) : null,
    },
  ]

  return (
    <div className="emotion-task-queue" style={{ marginTop: 24 }}>
      <Typography.Title level={5}>情绪任务队列</Typography.Title>

      <Space style={{ marginBottom: 12 }} wrap>
        <Tag>待处理 {counts.pending}</Tag>
        <Tag color="blue">执行中 {counts.running}</Tag>
        <Tag color="green">已完成 {counts.done}</Tag>
        <Tag color="red">失败 {counts.failed}</Tag>
      </Space>

      {error && <Alert type="error" showIcon title={error} style={{ marginBottom: 8 }} />}

      <Table<TrainingEmotionTask>
        rowKey={(task) => task.id}
        columns={columns}
        dataSource={tasks}
        pagination={false}
        locale={{ emptyText: <Empty description="暂无情绪识别任务" /> }}
      />
    </div>
  )
}
