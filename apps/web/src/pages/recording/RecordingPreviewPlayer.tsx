import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert,
  App,
  Button,
  Input,
  Modal,
  Slider,
  Space,
  Tooltip,
  Typography,
} from 'antd'
import {
  DownloadOutlined,
  FullscreenOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  SoundOutlined,
} from '@ant-design/icons'
import { apiErrorMessage, recordingClipUrl, recordingDownloadUrl } from '../../api/client'
import { formatPosition, parsePosition } from '../../utils/format'
import { isPlaybackSupported } from './recordingPlayer'
import {
  configureBufferForBitrate,
  destroyPlayer,
  pausePlayer,
  playRecording,
  probeSeekable,
  resumePlayer,
  seekToByteOffset,
} from './recordingPlayer'
import { seekUnbuffered } from './recordingSeek'

export interface RecordingPreviewPlayerProps {
  open: boolean
  roomId: number
  fileName: string
  fileSize: number
  onClose: () => void
}

function isBuffered(video: HTMLVideoElement, seconds: number): boolean {
  const buffered = video.buffered
  for (let index = 0; index < buffered.length; index += 1) {
    if (buffered.start(index) <= seconds && seconds < buffered.end(index)) {
      return true
    }
  }
  return false
}

export function RecordingPreviewPlayer({
  open,
  roomId,
  fileName,
  fileSize,
  onClose,
}: RecordingPreviewPlayerProps) {
  const { message } = App.useApp()
  const videoRef = useRef<HTMLVideoElement>(null)
  const [videoElement, setVideoElement] = useState<HTMLVideoElement | null>(null)

  // 稳定 callback ref：Modal 内容在 portal 中挂载，用 state 触发初始化 effect 且避免每次渲染重挂 ref
  const setVideoRef = useCallback((node: HTMLVideoElement | null) => {
    videoRef.current = node
    setVideoElement(node)
  }, [])

  const [playing, setPlaying] = useState(false)
  const [currentMs, setCurrentMs] = useState(0)
  const [durationMs, setDurationMs] = useState(0)
  const [volume, setVolume] = useState(100)
  const [muted, setMuted] = useState(false)
  const [seekable, setSeekable] = useState(true)
  const [seekNotice, setSeekNotice] = useState<string | null>(null)
  const [playbackError, setPlaybackError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const [jumpDraft, setJumpDraft] = useState('')
  const [startDraft, setStartDraft] = useState('')
  const [endDraft, setEndDraft] = useState('')
  const [startMs, setStartMs] = useState<number | null>(null)
  const [endMs, setEndMs] = useState<number | null>(null)
  const [downloading, setDownloading] = useState(false)

  const url = useMemo(() => recordingDownloadUrl(roomId, fileName), [roomId, fileName])
  const capabilityUnsupported = useMemo(
    () => fileName.startsWith('[AV1]') || !isPlaybackSupported(),
    [fileName],
  )
  const capabilityMessage = fileName.startsWith('[AV1]')
    ? 'AV1 编码暂不支持在线预览'
    : '当前浏览器不支持在线预览'

  const clipUrl =
    startMs !== null && endMs !== null
      ? recordingClipUrl(roomId, fileName, startMs, endMs)
      : null

  useEffect(() => {
    if (!open) {
      return
    }

    const video = videoElement
    setPlaying(false)
    setCurrentMs(0)
    setDurationMs(0)
    setVolume(100)
    setMuted(false)
    setSeekable(true)
    setSeekNotice(null)
    setPlaybackError(null)
    setJumpDraft('')
    setStartDraft('')
    setEndDraft('')
    setStartMs(null)
    setEndMs(null)

    if (!video || capabilityUnsupported) {
      return
    }

    let cancelled = false
    setLoading(true)
    const initialize = async () => {
      try {
        const rangeSeekable = await probeSeekable(url)
        if (cancelled) {
          return
        }

        setSeekable(rangeSeekable)
        if (!rangeSeekable) {
          setSeekNotice('该文件暂不支持跳转')
        }

        const bridgeSupported = await playRecording({ url, video, fileSize })
        if (cancelled) {
          destroyPlayer(url)
          return
        }

        if (!bridgeSupported) {
          setSeekable(false)
          setSeekNotice('该文件暂不支持跳转')
        }
      } catch {
        if (!cancelled) {
          setPlaybackError('预览暂不可用')
        }
      } finally {
        if (!cancelled) {
          setLoading(false)
        }
      }
    }

    void initialize()

    return () => {
      cancelled = true
      setLoading(false)
      destroyPlayer(url)
    }
  }, [open, url, fileSize, capabilityUnsupported, videoElement])

  const performSeek = async (targetMs: number) => {
    const video = videoRef.current
    if (!video || durationMs <= 0) {
      return
    }

    const target = Math.min(durationMs, Math.max(0, targetMs))
    const targetSeconds = target / 1000

    if (isBuffered(video, targetSeconds)) {
      video.currentTime = targetSeconds
      setCurrentMs(target)
      return
    }

    if (!seekable) {
      message.warning('该文件暂不支持跳转')
      return
    }

    const result = await seekUnbuffered({
      url,
      targetSeconds,
      durationSeconds: durationMs / 1000,
      fileSize,
    })

    if (!result) {
      message.warning('该文件暂不支持跳转')
      return
    }

    if (!seekToByteOffset(result.fileposition, result.milliseconds)) {
      message.error('预览暂不可用')
      return
    }

    setCurrentMs(result.milliseconds)
  }

  const togglePlay = () => {
    const video = videoRef.current
    if (!video) {
      return
    }

    if (video.paused) {
      void resumePlayer()
    } else {
      pausePlayer()
    }
  }

  const submitJump = () => {
    const parsed = parsePosition(jumpDraft)
    if (parsed === null) {
      message.error('时间格式无效')
      return
    }

    void performSeek(parsed)
  }

  const markStart = () => {
    const value = Math.max(0, Math.round(currentMs))
    setStartMs(value)
    setStartDraft(formatPosition(value))
  }

  const markEnd = () => {
    const value = Math.max(0, Math.round(currentMs))
    setEndMs(value)
    setEndDraft(formatPosition(value))
  }

  const commitStart = () => {
    if (!startDraft.trim()) {
      setStartMs(null)
      return
    }

    const parsed = parsePosition(startDraft)
    if (parsed === null) {
      message.error('时间格式无效')
      setStartDraft(startMs === null ? '' : formatPosition(startMs))
      return
    }

    if (endMs !== null && parsed >= endMs) {
      message.warning('起点必须早于终点')
      setStartDraft(startMs === null ? '' : formatPosition(startMs))
      return
    }

    setStartMs(parsed)
  }

  const commitEnd = () => {
    if (!endDraft.trim()) {
      setEndMs(null)
      return
    }

    const parsed = parsePosition(endDraft)
    if (parsed === null) {
      message.error('时间格式无效')
      setEndDraft(endMs === null ? '' : formatPosition(endMs))
      return
    }

    if (startMs !== null && parsed <= startMs) {
      message.warning('终点必须晚于起点')
      setEndDraft(endMs === null ? '' : formatPosition(endMs))
      return
    }

    setEndMs(parsed)
  }

  const clearRange = () => {
    setStartMs(null)
    setEndMs(null)
    setStartDraft('')
    setEndDraft('')
  }

  const downloadClip = async () => {
    if (!clipUrl || startMs === null || endMs === null) {
      message.warning('请先选择起点和终点')
      return
    }

    setDownloading(true)
    try {
      // <a download> 无法感知 4xx/5xx：先做一次轻量预检，响应头到达即取消响应体；
      // 非 2xx 提示错误且不触发下载，2xx 再交给浏览器流式下载片段。
      const response = await fetch(clipUrl)
      if (!response.ok) {
        let serverMessage = ''
        try {
          serverMessage = await response.text()
        } catch {
          // 忽略错误体读取失败
        }
        message.error(apiErrorMessage({ serverMessage }, '片段生成失败，请重试'))
        return
      }

      try {
        await response.body?.cancel()
      } catch {
        // 响应体已关闭：忽略
      }

      const anchor = document.createElement('a')
      anchor.href = clipUrl
      anchor.download = `${fileName}-${startMs}-${endMs}.mp4`
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
    } catch {
      message.error('网络异常，请重试')
    } finally {
      setDownloading(false)
    }
  }

  const onChangeVolume = (value: number) => {
    setVolume(value)
    const video = videoRef.current
    if (video) {
      video.volume = value / 100
      video.muted = false
    }
    setMuted(false)
  }

  const toggleMute = () => {
    const video = videoRef.current
    if (!video) {
      return
    }
    const next = !muted
    video.muted = next
    setMuted(next)
  }

  const requestFullscreen = () => {
    void videoRef.current?.requestFullscreen?.()
  }

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      destroyOnHidden
      width={880}
      title={`录播预览 · ${fileName}`}
    >
      <Space orientation="vertical" size={12} style={{ width: '100%' }}>
        {capabilityUnsupported && (
          <Alert type="warning" showIcon title={capabilityMessage} />
        )}
        {playbackError && <Alert type="error" showIcon title={playbackError} />}
        {seekNotice && <Alert type="warning" showIcon title={seekNotice} />}

        <video
          ref={setVideoRef}
          aria-label="录播预览播放器"
          data-testid="recording-preview-video"
          style={{ width: '100%', maxHeight: 480, background: '#000' }}
          onTimeUpdate={(event) => setCurrentMs(event.currentTarget.currentTime * 1000)}
          onDurationChange={(event) => {
            const duration = event.currentTarget.duration
            setDurationMs(Number.isFinite(duration) ? duration * 1000 : 0)
          }}
          onLoadedMetadata={(event) => {
            const duration = event.currentTarget.duration
            if (Number.isFinite(duration) && duration > 0) {
              setDurationMs(duration * 1000)
              configureBufferForBitrate(fileSize, duration)
            }
          }}
          onPlay={() => setPlaying(true)}
          onPause={() => setPlaying(false)}
          onError={() => setPlaybackError('预览暂不可用')}
        />

        <Space size={8} wrap>
          <Button
            aria-label={playing ? '暂停' : '播放'}
            icon={playing ? <PauseCircleOutlined /> : <PlayCircleOutlined />}
            onClick={togglePlay}
            disabled={capabilityUnsupported || loading}
          >
            {playing ? '暂停' : '播放'}
          </Button>
          <Typography.Text>
            {formatPosition(currentMs)} / {formatPosition(durationMs)}
          </Typography.Text>
          <Input
            aria-label="跳转时间"
            placeholder="m:ss"
            value={jumpDraft}
            onChange={(event) => setJumpDraft(event.target.value)}
            onPressEnter={submitJump}
            style={{ width: 90 }}
          />
          <Button onClick={submitJump} disabled={!durationMs}>
            跳转
          </Button>
        </Space>

        <Slider
          aria-label="播放进度"
          min={0}
          max={durationMs > 0 ? durationMs : 0}
          value={Math.min(currentMs, durationMs > 0 ? durationMs : 0)}
          disabled={!durationMs || !seekable}
          tooltip={{ formatter: (value) => formatPosition(value ?? 0) }}
          onChange={(value) => setCurrentMs(value)}
          onChangeComplete={(value) => void performSeek(value)}
        />

        <Space size={8} wrap>
          <Tooltip title="静音/取消静音" trigger={['hover', 'focus']}>
            <Button
              aria-label={muted ? '取消静音' : '静音'}
              icon={<SoundOutlined />}
              onClick={toggleMute}
            />
          </Tooltip>
          <Slider
            aria-label="音量"
            min={0}
            max={100}
            value={volume}
            onChange={onChangeVolume}
            style={{ width: 120 }}
          />
          <Tooltip title="全屏" trigger={['hover', 'focus']}>
            <Button aria-label="全屏" icon={<FullscreenOutlined />} onClick={requestFullscreen} />
          </Tooltip>
        </Space>

        <Space size={8} wrap>
          <Typography.Text strong>切片区间</Typography.Text>
          <Button onClick={markStart}>设为起点</Button>
          <Input
            aria-label="起点时间"
            placeholder="m:ss"
            value={startDraft}
            onChange={(event) => setStartDraft(event.target.value)}
            onBlur={commitStart}
            onPressEnter={commitStart}
            style={{ width: 90 }}
          />
          <Button onClick={markEnd}>设为终点</Button>
          <Input
            aria-label="终点时间"
            placeholder="m:ss"
            value={endDraft}
            onChange={(event) => setEndDraft(event.target.value)}
            onBlur={commitEnd}
            onPressEnter={commitEnd}
            style={{ width: 90 }}
          />
          <Button onClick={clearRange}>清空</Button>
        </Space>

        <Typography.Text type="secondary">
          起点会对齐到最近关键帧；实际片段起点可能比所选时间略早。
        </Typography.Text>
        <Typography.Text>
          {startMs === null && endMs === null
            ? '请选择起点与终点'
            : `区间：${startMs === null ? '未设置' : formatPosition(startMs)} - ${
                endMs === null ? '未设置' : formatPosition(endMs)
              }${
                startMs !== null && endMs !== null
                  ? `（时长 ${formatPosition(Math.max(0, endMs - startMs))}）`
                  : ''
              }`}
        </Typography.Text>

        <Button
          type="primary"
          icon={<DownloadOutlined />}
          aria-disabled={!clipUrl}
          loading={downloading}
          onClick={() => {
            if (!clipUrl) {
              message.warning('请先选择起点和终点')
              return
            }
            void downloadClip()
          }}
        >
          下载片段
        </Button>
      </Space>
    </Modal>
  )
}
