import { useEffect, useRef, useState, type RefObject } from 'react'
import { recordingAudioClipUrl } from '../../api/client'
import { formatPosition } from '../../utils/format'
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
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const [playingSegment, setPlayingSegment] = useState<TranscriptSegment | null>(null)
  const [currentTime, setCurrentTime] = useState(0)
  const [audioError, setAudioError] = useState(false)

  const canPlay = Boolean(task.file) && typeof task.roomId === 'number'

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
    if (!audio || !canPlay || !task.file || typeof task.roomId !== 'number') {
      return
    }
    setAudioError(false)
    setCurrentTime(0)
    audio.src = recordingAudioClipUrl(task.roomId, task.file, segment.start, segment.end)
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
      {(playingSegment || audioError) && (
        <div
          style={{
            display: 'flex',
            gap: '0.75rem',
            alignItems: 'center',
            padding: '0.5rem',
            marginBottom: '0.5rem',
            backgroundColor: '#1e293b',
            borderRadius: '0.25rem',
          }}
        >
          {playingSegment ? (
            <>
              <span style={{ color: '#94a3b8' }}>试听中</span>
              <span>
                {`${formatPosition(playingSegment.start)} - ${formatPosition(playingSegment.end)}`}
              </span>
              <span
                style={{
                  maxWidth: '24rem',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
              >
                {playingSegment.text}
              </span>
              <div
                role="progressbar"
                aria-label="试听进度"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={progress}
                style={{
                  flex: 1,
                  height: '0.5rem',
                  backgroundColor: '#334155',
                  borderRadius: '0.25rem',
                }}
              >
                <div
                  style={{
                    width: `${progress}%`,
                    height: '100%',
                    backgroundColor: '#38bdf8',
                    borderRadius: '0.25rem',
                  }}
                />
              </div>
              <button
                onClick={stopPlayback}
                aria-label="停止试听"
                style={{ padding: '0.25rem 0.75rem' }}
              >
                停止
              </button>
            </>
          ) : (
            <span style={{ color: '#f87171' }}>{AUDIO_UNAVAILABLE}</span>
          )}
        </div>
      )}

      <div ref={containerRef} style={{ maxHeight: '24rem', overflowY: 'auto' }}>
        <ul style={{ margin: 0, paddingLeft: '1rem' }}>
          {segments.map((segment) => {
            const isPlaying = playingSegment?.start === segment.start
            return (
              <li
                key={segment.start}
                style={isPlaying ? { backgroundColor: '#1e3a5f', borderRadius: '0.25rem' } : undefined}
              >
                <button
                  onClick={() => handleToggleSegment(segment)}
                  disabled={!canPlay}
                  title={canPlay ? undefined : AUDIO_UNAVAILABLE}
                  style={{ marginRight: '0.5rem', padding: '0.125rem 0.25rem' }}
                >
                  {isPlaying ? '停止' : '播放'}
                </button>
                <span style={{ color: '#94a3b8', marginRight: '0.5rem' }}>
                  {formatPosition(segment.start)}
                </span>
                <span>{segment.text}</span>
              </li>
            )
          })}
        </ul>
      </div>
    </>
  )
}
