import { useEffect, useRef, useState, type ReactNode } from 'react'
import {
  Alert,
  Button,
  Empty,
  Space,
  Spin,
  Table,
  Tag,
  Typography,
} from 'antd'
import { AudioOutlined, DownOutlined, UpOutlined } from '@ant-design/icons'
import type { ColumnsType } from 'antd/es/table'
import {
  listRecordingStatus,
  listRecordings,
  recordingAudioUrl,
  recordingDownloadUrl,
} from '../../api/client'
import type { components } from '../../api/schema'
import { PageHeader } from '../../components/PageHeader'

type RecordingStatusItem = components['schemas']['RecordingStatusItem']
type RecordingFile = components['schemas']['RecordingFile']

export const RECORDING_REFRESH_INTERVAL = 5000

function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }

  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 'B'
  for (const next of units) {
    if (value < 1024) {
      break
    }
    value /= 1024
    unit = next
  }

  return `${value.toFixed(1)} ${unit}`
}

const STATE_LABELS: Record<string, string> = {
  not_live: '未开播',
  recording: '录制中',
  error: '录制出错',
  unknown: '未知',
}

const STATE_COLORS: Record<string, string> = {
  not_live: 'default',
  recording: 'green',
  error: 'red',
  unknown: 'gold',
}

export function RecordingStatusPage() {
  const [items, setItems] = useState<RecordingStatusItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [expandedRoomId, setExpandedRoomId] = useState<number | null>(null)
  const [recordingsByRoom, setRecordingsByRoom] = useState<Record<number, RecordingFile[]>>({})
  const [recordingsLoading, setRecordingsLoading] = useState<number | null>(null)
  const [recordingsError, setRecordingsError] = useState<Record<number, string>>({})

  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const fetchStatus = async () => {
    try {
      setError(null)
      const data = await listRecordingStatus()
      setItems(data)
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchStatus()
    timerRef.current = setInterval(fetchStatus, RECORDING_REFRESH_INTERVAL)
    return () => {
      if (timerRef.current) {
        clearInterval(timerRef.current)
        timerRef.current = null
      }
    }
  }, [])

  const loadRecordings = async (roomId: number) => {
    setRecordingsError((prev) => {
      const next = { ...prev }
      delete next[roomId]
      return next
    })
    if (recordingsByRoom[roomId]) {
      return
    }
    try {
      setRecordingsLoading(roomId)
      const files = await listRecordings(roomId)
      setRecordingsByRoom((prev) => ({ ...prev, [roomId]: files }))
    } catch {
      setRecordingsError((prev) => ({ ...prev, [roomId]: '录播列表加载失败' }))
    } finally {
      setRecordingsLoading(null)
    }
  }

  const toggleRecordings = (roomId: number) => {
    if (expandedRoomId === roomId) {
      setExpandedRoomId(null)
      return
    }
    setExpandedRoomId(roomId)
    void loadRecordings(roomId)
  }

  const renderFileList = (roomId: number): ReactNode => {
    const files = recordingsByRoom[roomId]
    if (recordingsLoading === roomId) {
      return <Spin />
    }
    if (recordingsError[roomId]) {
      return <Typography.Text type="danger">{recordingsError[roomId]}</Typography.Text>
    }
    if (!files || files.length === 0) {
      return <Empty description="暂无历史录播" />
    }

    return (
      <Space direction="vertical" size={4}>
        {files.map((file) => (
          <Space key={file.name} size={8} wrap>
            <a href={recordingDownloadUrl(roomId, file.name)}>{file.name}</a>
            <Typography.Text type="secondary">{file.info.title}</Typography.Text>
            <Typography.Text type="secondary">{formatBytes(file.size)}</Typography.Text>
          </Space>
        ))}
      </Space>
    )
  }

  const rowKey = (item: RecordingStatusItem) => String(item.mid ?? item.roomId ?? '')

  const expandedKeys = items
    .filter(
      (item) =>
        item.roomId !== null && item.roomId !== undefined && item.roomId === expandedRoomId,
    )
    .map(rowKey)

  const columns: ColumnsType<RecordingStatusItem> = [
    {
      title: '主播',
      key: 'streamer',
      render: (_value, item) =>
        item.streamer?.name ? `${item.streamer.name}（${item.mid}）` : (item.mid ?? '-'),
    },
    { title: 'Room ID', dataIndex: 'roomId', width: 100, render: (value) => value ?? '-' },
    {
      title: '状态',
      key: 'state',
      width: 110,
      render: (_value, item) => {
        const state = item.state ?? 'unknown'
        return <Tag color={STATE_COLORS[state] ?? 'default'}>{STATE_LABELS[state] ?? state}</Tag>
      },
    },
    {
      title: '实例',
      dataIndex: 'hostName',
      width: 180,
      ellipsis: true,
      render: (value) => value ?? '-',
    },
    {
      title: '场次',
      dataIndex: 'sessionTitle',
      ellipsis: true,
      render: (value) => value ?? '-',
    },
    {
      title: '当前文件',
      key: 'files',
      render: (_value, item) => {
        const files = item.files ?? []
        const roomId = item.roomId ?? null
        if (files.length === 0) return '-'
        return (
          <Space direction="vertical" size={2}>
            {files.map((file) => (
              <span key={file}>
                {roomId ? <a href={recordingDownloadUrl(roomId, file)}>{file}</a> : file}
              </span>
            ))}
          </Space>
        )
      },
    },
    {
      title: '操作',
      key: 'action',
      width: 200,
      render: (_value, item) => {
        const roomId = item.roomId ?? null
        if (!roomId) return '-'
        const expanded = expandedRoomId === roomId
        return (
          <Space size={8}>
            {item.state === 'recording' && (
              <Button
                type="link"
                size="small"
                icon={<AudioOutlined />}
                href={recordingAudioUrl(roomId)}
                target="_blank"
                title="FLV 音频流，需支持 FLV 的播放器打开"
              >
                实时音频
              </Button>
            )}
            <Button
              size="small"
              icon={expanded ? <UpOutlined /> : <DownOutlined />}
              onClick={() => toggleRecordings(roomId)}
            >
              {expanded ? '收起录播' : '历史录播'}
            </Button>
          </Space>
        )
      },
    },
  ]

  return (
    <div>
      <PageHeader title="录制状态" description="按录播订阅查看录制状态与历史文件。" />

      {error && items.length === 0 ? (
        <Alert
          type="error"
          showIcon
          message={error}
          action={<Button onClick={fetchStatus}>重试</Button>}
        />
      ) : (
        <Table<RecordingStatusItem>
          rowKey={rowKey}
          columns={columns}
          dataSource={items}
          loading={loading}
          pagination={false}
          locale={{ emptyText: <Empty description="暂无录播订阅" /> }}
          expandable={{
            expandedRowKeys: expandedKeys,
            expandedRowRender: (item) =>
              item.roomId !== null && item.roomId !== undefined
                ? renderFileList(item.roomId)
                : null,
            showExpandColumn: false,
          }}
        />
      )}

      {error && !loading && items.length > 0 && (
        <Typography.Text type="danger" style={{ display: 'block', marginTop: 8 }}>
          状态刷新失败，展示的是上次结果
        </Typography.Text>
      )}
    </div>
  )
}
