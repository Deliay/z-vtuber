import { useEffect, useRef, useState } from 'react'
import { trainingAudioClipUrl } from '../../api/client'
import {
  WAVEFORM_PREVIEW_MAX_MS,
  defaultWaveformLoader,
  type WaveformLoader,
  type WaveformPlayer,
} from './waveformLoader'
import type { components } from '../../api/schema-training'

type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']

export interface DatasetItemWaveformProps {
  item: TrainingDatasetItem
  /** 可注入的波形加载器（默认 wavesurfer.js；测试注入假实现）。 */
  loader?: WaveformLoader
}

type LoadStatus = 'loading' | 'ready' | 'error'

/**
 * 单条音频数据的波形试听：区间 url 走 `trainingAudioClipUrl`
 * （`endMs` 为空时按试听预览上限 5 分钟兜底）；
 * 加载失败或 uri 不可解析 → 提示「音频不可用」，非阻断列表其他操作。
 */
export function DatasetItemWaveform({
  item,
  loader = defaultWaveformLoader,
}: DatasetItemWaveformProps) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const playerRef = useRef<WaveformPlayer | null>(null)
  const [status, setStatus] = useState<LoadStatus>('loading')

  const url = trainingAudioClipUrl(item.audioUri, item.startMs, item.endMs)
  const previewEndMs =
    item.endMs === null || item.endMs === undefined || item.endMs <= 0
      ? item.startMs + WAVEFORM_PREVIEW_MAX_MS
      : item.endMs

  useEffect(() => {
    const container = containerRef.current
    if (!container || !url) {
      playerRef.current = null
      setStatus('error')
      return
    }

    setStatus('loading')
    let player: WaveformPlayer
    try {
      player = loader.load({
        container,
        url,
        startMs: item.startMs,
        endMs: previewEndMs,
        onReady: () => setStatus('ready'),
        onError: () => setStatus('error'),
      })
    } catch {
      playerRef.current = null
      setStatus('error')
      return
    }

    playerRef.current = player
    return () => {
      playerRef.current = null
      player.destroy()
    }
  }, [loader, url, item.startMs, previewEndMs])

  return (
    <div className="dataset-item-waveform">
      <div data-testid="waveform-container" ref={containerRef} />
      {status === 'error' && <div role="alert">音频不可用</div>}
      {status === 'ready' && (
        <div style={{ display: 'flex', gap: '0.5rem', marginTop: '0.5rem' }}>
          <button onClick={() => playerRef.current?.play()}>播放</button>
          <button onClick={() => playerRef.current?.stop()}>停止</button>
        </div>
      )}
      {(item.endMs === null || item.endMs === undefined) && (
        <span style={{ color: '#94a3b8' }}>试听预览上限 5 分钟</span>
      )}
    </div>
  )
}
