import { useEffect, useRef, useState } from 'react'
import {
  listRecordingStatus,
  recordingAudioUrl,
  recordingDownloadUrl,
} from '../../api/client'
import type { components } from '../../api/schema'

type RecordingStatusItem = components['schemas']['RecordingStatusItem']

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

  const renderFiles = (item: RecordingStatusItem) => {
    const files = item.files ?? []
    if (files.length === 0) {
      return '-'
    }

    return (
      <ul style={{ margin: 0, paddingLeft: '1rem' }}>
        {files.map((file) => (
          <li key={file}>
            <a href={recordingDownloadUrl(item.roomId!, file)}>{file}</a>
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
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>文件名</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => {
              const state = item.state ?? 'unknown'
              return (
                <tr key={item.mid}>
                  <td style={{ padding: '0.5rem' }}>
                    {item.streamer?.name
                      ? `${item.streamer.name}（${item.mid}）`
                      : (item.mid ?? '-')}
                  </td>
                  <td style={{ padding: '0.5rem' }}>{item.roomId ?? '-'}</td>
                  <td style={{ padding: '0.5rem' }}>
                    <span style={{ color: STATE_COLORS[state] ?? '#e2e8f0' }}>
                      {STATE_LABELS[state] ?? state}
                    </span>
                  </td>
                  <td style={{ padding: '0.5rem' }}>{item.hostName ?? '-'}</td>
                  <td style={{ padding: '0.5rem' }}>{item.sessionTitle ?? '-'}</td>
                  <td style={{ padding: '0.5rem' }}>{renderFiles(item)}</td>
                  <td style={{ padding: '0.5rem' }}>
                    {state === 'recording' && item.roomId ? (
                      <a href={recordingAudioUrl(item.roomId)}>实时音频</a>
                    ) : (
                      '-'
                    )}
                  </td>
                </tr>
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
