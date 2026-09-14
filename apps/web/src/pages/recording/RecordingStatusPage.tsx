import { useEffect, useRef, useState, type ReactNode } from 'react'
import {
  listRecordingStatus,
  listRecordings,
  recordingAudioUrl,
  recordingDownloadUrl,
} from '../../api/client'
import type { components } from '../../api/schema'

type RecordingStatusItem = components['schemas']['RecordingStatusItem']
type RecordingFile = components['schemas']['RecordingFile']

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
  not_live: '#94a3b8',
  recording: '#22c55e',
  error: '#f87171',
  unknown: '#eab308',
}

export const RECORDING_REFRESH_INTERVAL = 5000

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

  const toggleRecordings = async (roomId: number) => {
    if (expandedRoomId === roomId) {
      setExpandedRoomId(null)
      return
    }

    setExpandedRoomId(roomId)
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

  const renderFileList = (roomId: number) => {
    const files = recordingsByRoom[roomId]
    if (recordingsLoading === roomId) {
      return <p style={{ margin: 0 }}>加载中...</p>
    }
    if (recordingsError[roomId]) {
      return <p style={{ margin: 0, color: '#f87171' }}>{recordingsError[roomId]}</p>
    }
    if (!files || files.length === 0) {
      return <p style={{ margin: 0 }}>暂无历史录播</p>
    }

    return (
      <ul style={{ margin: 0, paddingLeft: '1rem' }}>
        {files.map((file) => (
          <li key={file.name}>
            <a href={recordingDownloadUrl(roomId, file.name)}>{file.name}</a>{' '}
            <span style={{ color: '#94a3b8', fontSize: '0.875rem' }}>
              {formatBytes(file.size)}
            </span>
          </li>
        ))}
      </ul>
    )
  }

  return (
    <div>
      <h2>录制状态</h2>

      {loading && items.length === 0 ? (
        <p>加载中...</p>
      ) : error && items.length === 0 ? (
        <p style={{ color: '#f87171' }}>{error}</p>
      ) : items.length === 0 ? (
        <p>暂无录播订阅</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>主播</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>Room ID</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>状态</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>实例</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>场次</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>当前文件</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => {
              const state = item.state ?? 'unknown'
              const roomId = item.roomId ?? null
              const files = item.files ?? []

              return (
                <FragmentRow
                  key={item.mid}
                  item={item}
                  state={state}
                  roomId={roomId}
                  files={files}
                  expanded={roomId !== null && expandedRoomId === roomId}
                  onToggle={toggleRecordings}
                  renderFileList={renderFileList}
                />
              )
            })}
          </tbody>
        </table>
      )}

      {error && !loading && items.length > 0 && (
        <p style={{ color: '#f87171', fontSize: '0.875rem' }}>
          状态刷新失败，展示的是上次结果
        </p>
      )}
    </div>
  )
}

interface FragmentRowProps {
  item: RecordingStatusItem
  state: string
  roomId: number | null
  files: string[]
  expanded: boolean
  onToggle: (roomId: number) => void
  renderFileList: (roomId: number) => ReactNode
}

function FragmentRow({ item, state, roomId, files, expanded, onToggle, renderFileList }: FragmentRowProps) {
  return (
    <>
      <tr>
        <td style={{ padding: '0.5rem' }}>
          {item.streamer?.name
            ? `${item.streamer.name}（${item.mid}）`
            : (item.mid ?? '-')}
        </td>
        <td style={{ padding: '0.5rem' }}>{roomId ?? '-'}</td>
        <td style={{ padding: '0.5rem' }}>
          <span style={{ color: STATE_COLORS[state] ?? '#e2e8f0' }}>
            {STATE_LABELS[state] ?? state}
          </span>
        </td>
        <td style={{ padding: '0.5rem' }}>{item.hostName ?? '-'}</td>
        <td style={{ padding: '0.5rem' }}>{item.sessionTitle ?? '-'}</td>
        <td style={{ padding: '0.5rem' }}>
          {files.length === 0 ? (
            '-'
          ) : (
            <ul style={{ margin: 0, paddingLeft: '1rem' }}>
              {files.map((file) => (
                <li key={file}>
                  {roomId ? (
                    <a href={recordingDownloadUrl(roomId, file)}>{file}</a>
                  ) : (
                    file
                  )}
                </li>
              ))}
            </ul>
          )}
        </td>
        <td style={{ padding: '0.5rem' }}>
          {state === 'recording' && roomId ? (
            <a
              href={recordingAudioUrl(roomId)}
              title="FLV 音频流，需支持 FLV 的播放器打开"
            >
              实时音频
            </a>
          ) : (
            '-'
          )}
          {roomId && (
            <>
              {' '}
              <button
                onClick={() => onToggle(roomId)}
                style={{ padding: '0.25rem 0.5rem' }}
              >
                {expanded ? '收起录播' : '历史录播'}
              </button>
            </>
          )}
        </td>
      </tr>
      {expanded && roomId && (
        <tr>
          <td colSpan={7} style={{ padding: '0.5rem', backgroundColor: 'rgba(148,163,184,0.08)' }}>
            {renderFileList(roomId)}
          </td>
        </tr>
      )}
    </>
  )
}
