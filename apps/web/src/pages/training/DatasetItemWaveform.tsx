import { useCallback, useEffect, useRef, useState } from 'react'
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
 * 加载就绪后**自动开始播放**（播放区间仍是片段相对时间轴 `0 → (endMs-startMs)/1000`，
 * 由 `waveformLoader` 负责），「播放/停止」按钮用于重播/中止；
 * 播放失败（`play()` reject/抛错）非阻断提示（参考 `SegmentAudioPlayer` 的 400/404 降级先例）；
 * 加载失败或 uri 不可解析 → 提示「音频不可用」，非阻断列表其他操作。
 */
export function DatasetItemWaveform({
  item,
  loader = defaultWaveformLoader,
}: DatasetItemWaveformProps) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const playerRef = useRef<WaveformPlayer | null>(null)
  const [status, setStatus] = useState<LoadStatus>('loading')
  const [playFailed, setPlayFailed] = useState(false)

  const url = trainingAudioClipUrl(item.audioUri, item.startMs, item.endMs)
  const previewEndMs =
    item.endMs === null || item.endMs === undefined || item.endMs <= 0
      ? item.startMs + WAVEFORM_PREVIEW_MAX_MS
      : item.endMs

  /**
   * 播放指定片段。真实 loader 返回播放 Promise；同步实现可能直接抛错。
   * 两者都只在**播放失败**时置 `playFailed` 提示（保留「播放/停止」控件供重试），
   * 不映射为加载失败 `onError`，不打断其他操作。
   */
  const startPlayback = useCallback((player: WaveformPlayer | null) => {
    if (!player) return
    setPlayFailed(false)
    try {
      const result = player.play()
      if (result && typeof (result as Promise<void>).catch === 'function') {
        ;(result as Promise<void>).catch(() => setPlayFailed(true))
      }
    } catch {
      setPlayFailed(true)
    }
  }, [])

  useEffect(() => {
    const container = containerRef.current
    if (!container || !url) {
      playerRef.current = null
      setStatus('error')
      return
    }

    setStatus('loading')
    setPlayFailed(false)
    let player: WaveformPlayer | null = null
    // 本次加载是否仍为当前实例：切换条目/卸载后置 false，自动播放不串播上一条
    let active = true
    try {
      player = loader.load({
        container,
        url,
        startMs: item.startMs,
        endMs: previewEndMs,
        // 就绪即自动播放；微任务延迟确保 player 已赋值（同步假实现与异步 wavesurfer 一致）
        onReady: () => {
          setStatus('ready')
          queueMicrotask(() => {
            if (active) startPlayback(player)
          })
        },
        onError: () => setStatus('error'),
      })
    } catch {
      playerRef.current = null
      setStatus('error')
      return
    }

    playerRef.current = player
    return () => {
      active = false
      playerRef.current = null
      player?.destroy()
    }
  }, [loader, url, item.startMs, previewEndMs, startPlayback])

  return (
    <div className="dataset-item-waveform">
      <div data-testid="waveform-container" ref={containerRef} />
      {status === 'error' && <div role="alert">音频不可用</div>}
      {status === 'ready' && playFailed && (
        <div role="alert">音频暂不可用，请点击播放重试</div>
      )}
      {status === 'ready' && (
        <div style={{ display: 'flex', gap: '0.5rem', marginTop: '0.5rem' }}>
          <button onClick={() => startPlayback(playerRef.current)}>播放</button>
          <button onClick={() => playerRef.current?.stop()}>停止</button>
        </div>
      )}
      {(item.endMs === null || item.endMs === undefined) && (
        <span style={{ color: '#94a3b8' }}>试听预览上限 5 分钟</span>
      )}
    </div>
  )
}
