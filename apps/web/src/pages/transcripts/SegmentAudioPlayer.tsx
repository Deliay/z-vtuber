import { useEffect, useRef, useState, type RefObject } from 'react'
import { Alert, Button, Card, Flex, Progress, Space, Tag, theme, Tooltip, Typography } from 'antd'
import { recordingAudioClipUrl } from '../../api/client'
import { formatPosition } from '../../utils/format'
import { TruncatedText } from '../../components/TruncatedText'
import type { components } from '../../api/schema-recording-transcript'

type TranscriptTask = components['schemas']['TranscriptTask']
type TranscriptSegment = components['schemas']['TranscriptSegment']

const AUDIO_UNAVAILABLE = '音频暂不可用'

export interface SegmentAudioPlayerProps {
  task: TranscriptTask
  segments: TranscriptSegment[]
  /** 识别结果限高滚动容器（自动底部跟随由使用方持有 ref）。 */
  containerRef?: RefObject<HTMLDivElement | null>
}

/**
 * 识别片段试听：共享单例 `<audio>`（同一时刻只播一句）+ 统一播放控件
 * （当前句起止时间与文字、进度、停止）+ 片段行「播放/停止」按钮。
 * 生命周期：ended/pause 清高亮；切换任务、收起（卸载）→ pause() 并清空 src。
 */
export function SegmentAudioPlayer({ task, segments, containerRef }: SegmentAudioPlayerProps) {
  const { token } = theme.useToken()
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const [playingSegment, setPlayingSegment] = useState<TranscriptSegment | null>(null)
  const [currentTime, setCurrentTime] = useState(0)
  const [audioError, setAudioError] = useState(false)

  const canPlay = Boolean(task.file) && typeof task.roomId === 'number'

  // 负偏移可能把整句推到时间轴起点之前：end <= 0 的片段没有可试听的音频区间
  const canPlaySegment = (segment: TranscriptSegment) => canPlay && segment.end > 0

  // 共享单例 audio：挂载时创建并挂载生命周期监听，卸载时停止并释放。
  useEffect(() => {
    const audio = new Audio()
    audio.preload = 'auto'
    audioRef.current = audio

    const handleEnded = () => {
      setPlayingSegment(null)
      setCurrentTime(0)
    }
    const handlePause = () => {
      setPlayingSegment(null)
      setCurrentTime(0)
    }
    const handleError = () => {
      setPlayingSegment(null)
      setCurrentTime(0)
      setAudioError(true)
    }
    const handleTimeUpdate = () => {
      setCurrentTime(audio.currentTime)
    }

    audio.addEventListener('ended', handleEnded)
    audio.addEventListener('pause', handlePause)
    audio.addEventListener('error', handleError)
    audio.addEventListener('timeupdate', handleTimeUpdate)

    return () => {
      audio.removeEventListener('ended', handleEnded)
      audio.removeEventListener('pause', handlePause)
      audio.removeEventListener('error', handleError)
      audio.removeEventListener('timeupdate', handleTimeUpdate)
      audio.pause()
      audio.removeAttribute('src')
      audioRef.current = null
    }
  }, [])

  // 切换选中任务：停止播放并清空 src，避免残留声音。
  useEffect(() => {
    return () => {
      const audio = audioRef.current
      if (audio) {
        audio.pause()
        audio.removeAttribute('src')
      }
      setPlayingSegment(null)
      setCurrentTime(0)
      setAudioError(false)
    }
  }, [task.id])

  const stopPlayback = () => {
    const audio = audioRef.current
    if (audio) {
      audio.pause()
      audio.removeAttribute('src')
    }
    setPlayingSegment(null)
    setCurrentTime(0)
  }

  const playSegment = (segment: TranscriptSegment) => {
    const audio = audioRef.current
    if (!audio || !canPlaySegment(segment) || !task.file || typeof task.roomId !== 'number') {
      return
    }
    setAudioError(false)
    setCurrentTime(0)
    // 展示时间可能为负（负偏移）：试听区间钳到 [0, 正无穷)，展示起止时间不变
    const startMs = Math.max(0, segment.start)
    const endMs = Math.max(startMs, segment.end)
    audio.src = recordingAudioClipUrl(task.roomId, task.file, startMs, endMs)
    setPlayingSegment(segment)
    const playResult = audio.play()
    if (playResult !== undefined && typeof playResult.catch === 'function') {
      // play() 失败（400 录制中 / 404 文件不存在等）→ 非阻断错误提示。
      playResult.catch(() => {
        setAudioError(true)
        setPlayingSegment(null)
        setCurrentTime(0)
      })
    }
  }

  const handleToggleSegment = (segment: TranscriptSegment) => {
    if (playingSegment && playingSegment.start === segment.start) {
      stopPlayback()
    } else {
      playSegment(segment)
    }
  }

  const durationMs = playingSegment ? Math.max(playingSegment.end - playingSegment.start, 0) : 0
  const progress =
    durationMs > 0 ? Math.min(100, Math.floor(((currentTime * 1000) / durationMs) * 100)) : 0

  return (
    <>
      {playingSegment && (
        <Card
          size="small"
          style={{ marginBottom: 8 }}
          styles={{ body: { padding: 8 } }}
        >
          <Flex gap={12} align="center" wrap>
            <Tag color="processing">试听中</Tag>
            <Typography.Text>
              {`${formatPosition(playingSegment.start)} - ${formatPosition(playingSegment.end)}`}
            </Typography.Text>
            <TruncatedText
              text={playingSegment.text}
              style={{ maxWidth: '24rem', flex: 1 }}
            />
            <Progress
              aria-label="试听进度"
              percent={progress}
              showInfo={false}
              style={{ flex: 1, minWidth: 120, margin: 0 }}
            />
            <Button onClick={stopPlayback} aria-label="停止试听">
              停止
            </Button>
          </Flex>
        </Card>
      )}
      {audioError && !playingSegment && (
        <Alert type="error" showIcon title={AUDIO_UNAVAILABLE} style={{ marginBottom: 8 }} />
      )}

      <div
        ref={containerRef}
        className="segment-audio-list"
        style={{ maxHeight: '24rem', overflowY: 'auto' }}
      >
        <ul style={{ margin: 0, paddingLeft: 16 }}>
          {segments.map((segment) => {
            const isPlaying = playingSegment?.start === segment.start
            const playable = canPlaySegment(segment)
            const playButton = (
              <Button
                size="small"
                onClick={() => handleToggleSegment(segment)}
                disabled={!playable}
              >
                {isPlaying ? '停止' : '播放'}
              </Button>
            )
            return (
              <li
                key={segment.start}
                className={isPlaying ? 'segment-audio-playing' : undefined}
                style={{
                  padding: '2px 4px',
                  borderRadius: 4,
                  background: isPlaying ? token.colorPrimaryBg : undefined,
                }}
              >
                <Space size={8} align="start">
                  {playable ? (
                    playButton
                  ) : (
                    // 置灰说明对键盘用户同样可达：外包可聚焦 span，focus 即出 Tooltip。
                    <Tooltip title={AUDIO_UNAVAILABLE} trigger={['hover', 'focus']}>
                      <span tabIndex={0} style={{ display: 'inline-flex' }}>
                        {playButton}
                      </span>
                    </Tooltip>
                  )}
                  <Typography.Text type="secondary">
                    {formatPosition(segment.start)}
                  </Typography.Text>
                  <Typography.Text>{segment.text}</Typography.Text>
                </Space>
              </li>
            )
          })}
        </ul>
      </div>
    </>
  )
}
