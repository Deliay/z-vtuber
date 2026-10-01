import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert,
  App as AntdApp,
  Button,
  Card,
  Empty,
  Flex,
  Form,
  InputNumber,
  Select,
  Space,
  Table,
  Tag,
  theme,
  Tooltip,
  Typography,
} from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import { TruncatedText } from '../../components/TruncatedText'
import type { ColumnsType } from 'antd/es/table'
import {
  listTranscriptTasks,
  getTranscriptTask,
  openTranscriptStream,
  listTranscriptRecordings,
  listTranscriptModels,
  getRoomTranscripts,
  enqueueTranscript,
  retryTranscriptTask,
  transcriptSubtitleUrl,
  updateTranscriptTaskOffset,
} from '../../api/client'
import type { components as transcriptComponents } from '../../api/schema-recording-transcript'
import { formatBytes, formatDateTime, formatPosition } from '../../utils/format'
import { SegmentAudioPlayer } from './SegmentAudioPlayer'

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
  PENDING: 'gold',
  RUNNING: 'green',
  DONE: 'blue',
  FAILED: 'red',
}

const STATUS_ORDER: Record<string, number> = {
  RUNNING: 0,
  PENDING: 1,
  FAILED: 2,
  DONE: 3,
}

/** 播放偏移合法范围 ±24h（秒），与契约 UpdateTranscriptTaskOffsetRequest 一致。 */
const MAX_OFFSET_SECONDS = 86_400

/** 任务播放偏移（毫秒）→ 输入框秒值回显（0.1 秒精度无损）。 */
function formatOffsetSeconds(offsetMs: number | undefined): string {
  return String((offsetMs ?? 0) / 1000)
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
  const { message } = AntdApp.useApp()
  const { token } = theme.useToken()
  const [tasks, setTasks] = useState<TranscriptTask[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [recordings, setRecordings] = useState<TranscriptRecordingItem[]>([])
  const [models, setModels] = useState<TranscriptModel[]>([])

  const [enqueueRecordingId, setEnqueueRecordingId] = useState('')
  const [enqueueModelId, setEnqueueModelId] = useState('')
  const [enqueuing, setEnqueuing] = useState(false)

  const [selectedRoomId, setSelectedRoomId] = useState('')
  const [roomView, setRoomView] = useState<RoomTranscriptView | null>(null)
  const [roomError, setRoomError] = useState<string | null>(null)

  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null)
  const selectedTask = useMemo(
    () => tasks.find((item) => item.id === selectedTaskId) ?? null,
    [tasks, selectedTaskId],
  )
  const [refreshKey, setRefreshKey] = useState(0)
  const loadedTaskIdRef = useRef<string | null>(null)
  const [segments, setSegments] = useState<TranscriptSegment[]>([])
  const [segmentsLoading, setSegmentsLoading] = useState(false)
  const [segmentsError, setSegmentsError] = useState<string | null>(null)
  const segmentsContainerRef = useRef<HTMLDivElement | null>(null)

  const [offsetDraft, setOffsetDraft] = useState('')
  const [offsetError, setOffsetError] = useState<string | null>(null)
  const [offsetSaving, setOffsetSaving] = useState(false)

  // 当前生效偏移回显：选中任务或服务端偏移变化时同步输入框（用户编辑过程不被打断）
  useEffect(() => {
    setOffsetDraft(formatOffsetSeconds(selectedTask?.offsetMs))
    setOffsetError(null)
  }, [selectedTask?.id, selectedTask?.offsetMs])

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
          label: anchorName ? `${anchorName}（${recording.roomId}）` : String(recording.roomId),
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

  const handleEnqueue = async () => {
    if (!enqueueRecordingId || !enqueueModelId) {
      message.error('请选择录播场次与模型')
      return
    }

    setEnqueuing(true)
    try {
      const result = await enqueueTranscript(enqueueRecordingId, enqueueModelId)
      message.success(`已创建任务 ${result.taskId}`)
      await fetchTasks()
      if (selectedRoomId) {
        const view = await getRoomTranscripts(Number(selectedRoomId))
        setRoomView(view)
      }
    } catch (err) {
      const existingTaskId = parseExistingTaskId(err)
      if (existingTaskId) {
        message.info(`已有任务 ${existingTaskId}`)
        return
      }
      message.error('发起识别失败')
    } finally {
      setEnqueuing(false)
    }
  }

  const handleQueryRoom = async () => {
    if (!selectedRoomId) {
      message.error('请选择主播')
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

  const handleSaveOffset = async () => {
    if (!selectedTaskId || offsetSaving) {
      return
    }

    const raw = offsetDraft.trim()
    const seconds = Number(raw)
    if (raw === '' || !Number.isFinite(seconds) || Math.abs(seconds) > MAX_OFFSET_SECONDS) {
      setOffsetError('偏移无效')
      return
    }

    setOffsetSaving(true)
    try {
      const updated = await updateTranscriptTaskOffset(selectedTaskId, Math.round(seconds * 1000))
      setTasks((prev) =>
        prev.map((item) => (item.id === selectedTaskId ? { ...item, ...updated } : item)),
      )
      setOffsetError(null)
      // 偏移变更后旧口径片段会残留：清空已合并片段，重拉详情并重建 SSE（refreshKey 触发既有 effect）
      setSegments([])
      setRefreshKey((key) => key + 1)
    } catch {
      // 服务端 400（越界等）：提示无效并保持原值
      setOffsetError('偏移无效')
      setOffsetDraft(formatOffsetSeconds(selectedTask?.offsetMs))
    } finally {
      setOffsetSaving(false)
    }
  }

  const taskColumns: ColumnsType<TranscriptTask> = [
    {
      title: '状态',
      key: 'status',
      width: 100,
      render: (_value, task) => {
        const status = task.status ?? 'PENDING'
        return <Tag color={STATUS_COLORS[status] ?? 'default'}>{STATUS_LABELS[status] ?? status}</Tag>
      },
    },
    {
      title: '场次',
      key: 'title',
      render: (_value, task) => (
        <TruncatedText text={task.title ?? task.recordingId ?? '-'} />
      ),
    },
    {
      title: '模型',
      key: 'model',
      width: 160,
      render: (_value, task) => (
        <TruncatedText text={task.modelName ?? task.modelId ?? '-'} />
      ),
    },
    {
      title: '进度',
      key: 'position',
      width: 100,
      render: (_value, task) => formatPosition(task.position),
    },
    {
      title: '操作',
      key: 'action',
      width: 120,
      render: (_value, task) => (
        <Button
          size="small"
          onClick={() => handleTaskAction(task.id)}
          aria-label={
            selectedTaskId === task.id
              ? `刷新任务-${task.id ?? ''}`
              : `查看任务-${task.id ?? ''}`
          }
        >
          {selectedTaskId === task.id ? '刷新' : '查看'}
        </Button>
      ),
    },
  ]

  return (
    <div>
      <Space style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }}>
        <Typography.Title level={5} style={{ margin: 0 }}>
          任务列表
        </Typography.Title>
        <Button icon={<ReloadOutlined />} onClick={fetchTasks}>
          刷新
        </Button>
      </Space>

      {error && tasks.length === 0 ? (
        <Alert
          type="error"
          showIcon
          title={error}
          action={<Button onClick={fetchTasks}>重试</Button>}
        />
      ) : (
        <Table<TranscriptTask>
          rowKey={(task) => task.id ?? ''}
          columns={taskColumns}
          dataSource={tasks}
          loading={loading}
          pagination={false}
          locale={{ emptyText: <Empty description="暂无转写任务" /> }}
        />
      )}
      {error && !loading && tasks.length > 0 && (
        <Typography.Text type="danger" style={{ display: 'block', marginTop: 8 }}>
          任务刷新失败，展示的是上次结果
        </Typography.Text>
      )}

      {selectedTaskId && selectedTask && (
        <Card size="small" style={{ marginTop: 24 }}>
          <Space style={{ marginBottom: 8, width: '100%', justifyContent: 'space-between' }} wrap>
            <Typography.Title level={5} style={{ margin: 0 }}>
              识别结果
            </Typography.Title>
            <Space wrap size={8}>
              <Typography.Text type="secondary">播放偏移</Typography.Text>
              <InputNumber
                aria-label="播放偏移（秒）"
                step={0.1}
                value={offsetDraft === '' ? null : Number(offsetDraft)}
                onChange={(value) => {
                  setOffsetDraft(value === null || value === undefined ? '' : String(value))
                  setOffsetError(null)
                }}
                style={{ width: 96 }}
              />
              <Typography.Text type="secondary">秒（正值=识别提前需后移）</Typography.Text>
              <Button
                onClick={handleSaveOffset}
                aria-label="保存播放偏移"
                loading={offsetSaving}
              >
                保存
              </Button>
              {(['srt', 'ass'] as const).map((format) => {
                const disabled = segments.length === 0
                const link = (
                  <Typography.Link
                    key={format}
                    href={transcriptSubtitleUrl(selectedTaskId, format)}
                    download
                    aria-disabled={disabled ? true : undefined}
                    style={disabled ? { color: token.colorTextDisabled } : undefined}
                    onClick={(event) => {
                      if (disabled) {
                        event.preventDefault()
                      }
                    }}
                  >
                    {format === 'srt' ? '下载 SRT' : '下载 ASS'}
                  </Typography.Link>
                )
                // 置灰说明对键盘用户同样可达（focus 即出 Tooltip）。
                return disabled ? (
                  <Tooltip key={format} title="暂无识别结果" trigger={['hover', 'focus']}>
                    {link}
                  </Tooltip>
                ) : (
                  link
                )
              })}
              <Button onClick={() => setSelectedTaskId(null)} aria-label="收起识别结果">
                收起
              </Button>
            </Space>
          </Space>
          {offsetError && (
            <Alert type="error" showIcon title={offsetError} style={{ marginBottom: 8 }} />
          )}
          {segmentsLoading && segments.length === 0 ? (
            <Typography.Text type="secondary">加载中...</Typography.Text>
          ) : segmentsError && segments.length === 0 ? (
            <Typography.Text type="danger">{segmentsError}</Typography.Text>
          ) : segments.length === 0 ? (
            <Empty description="暂无识别结果" />
          ) : (
            <SegmentAudioPlayer
              task={selectedTask}
              segments={segments}
              containerRef={segmentsContainerRef}
            />
          )}
        </Card>
      )}

      <Typography.Title level={5} style={{ marginTop: 32 }}>
        手动发起识别
      </Typography.Title>
      <Form layout="inline" style={{ rowGap: 12 }}>
        <Form.Item style={{ marginBottom: 0 }}>
          <Select
            aria-label="录播场次"
            placeholder="选择录播场次"
            value={enqueueRecordingId || undefined}
            onChange={setEnqueueRecordingId}
            style={{ width: 360 }}
            options={recordings.map((recording) => {
              const startTime = recording.startedAt ? formatDateTime(recording.startedAt) : '-'
              const endTime = recording.endedAt ? formatDateTime(recording.endedAt) : '进行中'
              return {
                value: recording.id as string,
                label: `[${recording.anchorName ?? recording.roomId}][${formatBytes(
                  recording.size,
                )}] ${recording.title ?? recording.id} - ${startTime} - ${endTime}`,
              }
            })}
          />
        </Form.Item>
        <Form.Item style={{ marginBottom: 0 }}>
          <Select
            aria-label="识别模型"
            placeholder="选择模型"
            value={enqueueModelId || undefined}
            onChange={setEnqueueModelId}
            style={{ width: 180 }}
            options={enabledModels.map((model) => ({
              value: model.id as string,
              label: model.name as string,
            }))}
          />
        </Form.Item>
        <Form.Item style={{ marginBottom: 0 }}>
          <Button type="primary" loading={enqueuing} onClick={handleEnqueue}>
            发起识别
          </Button>
        </Form.Item>
      </Form>

      <Typography.Title level={5} style={{ marginTop: 32 }}>
        场次任务
      </Typography.Title>
      <Space style={{ marginBottom: 8 }} wrap>
        <Select
          aria-label="主播"
          placeholder="选择主播"
          value={selectedRoomId || undefined}
          onChange={setSelectedRoomId}
          style={{ width: 200 }}
          options={roomOptions.map((option) => ({
            value: String(option.roomId),
            label: option.label,
          }))}
        />
        <Button onClick={handleQueryRoom}>查询</Button>
      </Space>
      {roomError && <Alert type="error" showIcon title={roomError} style={{ marginBottom: 8 }} />}
      {roomView && (
        <div>
          <Typography.Text type="secondary">
            自动识别：{roomView.autoEnqueueEnabled ? '已开启' : '已关闭'}
          </Typography.Text>
          {roomView.items.length === 0 ? (
            <Empty description="暂无场次" />
          ) : (
            roomView.items.map((item) => (
              <Card
                key={item.recordingId}
                size="small"
                title={item.title ?? item.recordingId}
                style={{ marginBottom: 8 }}
              >
                {item.tasks.length === 0 ? (
                  <Typography.Text type="secondary">暂无任务</Typography.Text>
                ) : (
                  <Space orientation="vertical" size={4} style={{ width: '100%' }}>
                    {item.tasks.map((task) => (
                      <Flex
                        key={task.taskId ?? `${task.modelId}-${task.status}`}
                        align="center"
                        justify="space-between"
                        gap={8}
                      >
                        <Typography.Text
                          style={{
                            color:
                              task.status === 'FAILED'
                                ? token.colorTextSecondary
                                : undefined,
                          }}
                        >
                          {`${task.modelName ?? task.modelId} - ${
                            STATUS_LABELS[task.status ?? ''] ?? task.status
                          } - ${formatPosition(task.position)}`}
                        </Typography.Text>
                        {task.status === 'FAILED' && (
                          <Button
                            size="small"
                            onClick={() => handleRetry(task.taskId ?? '')}
                          >
                            重试
                          </Button>
                        )}
                      </Flex>
                    ))}
                  </Space>
                )}
              </Card>
            ))
          )}
        </div>
      )}
    </div>
  )
}
