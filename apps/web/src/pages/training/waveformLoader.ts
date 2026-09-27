import type WaveSurfer from 'wavesurfer.js'

/** 试听预览上限：`endMs` 为空时最多加载 `startMs` 之后的 5 分钟。 */
export const WAVEFORM_PREVIEW_MAX_MS = 300000

export interface WaveformLoadOptions {
  /** 波形容器（组件在挂载时提供）。 */
  container: HTMLElement
  /** 区间试听地址（`trainingAudioClipUrl`）。 */
  url: string
  startMs: number
  endMs: number
  /** 音频加载完成、可播放。 */
  onReady?: () => void
  /** 音频加载失败（提示「音频不可用」，不阻断列表操作）。 */
  onError?: () => void
}

export interface WaveformPlayer {
  /** 按 `[startMs, endMs)` 区间播放。 */
  play(): void
  /** 停止播放并回到起点。 */
  stop(): void
  /** 释放波形实例（组件卸载时调用）。 */
  destroy(): void
}

export interface WaveformLoader {
  load(options: WaveformLoadOptions): WaveformPlayer
}

/**
 * wavesurfer.js 加载器：动态 `import` 以免测试环境（jsdom）加载真实波形库，
 * 测试通过注入 `WaveformLoader` 假实现驱动（参考 `SegmentAudioPlayer` 的策略）。
 */
export const defaultWaveformLoader: WaveformLoader = {
  load({ container, url, startMs, endMs, onReady, onError }) {
    let destroyed = false
    let instance: WaveSurfer | null = null

    void import('wavesurfer.js')
      .then(({ default: WaveSurfer }) => {
        if (destroyed) {
          return
        }
        const ws = WaveSurfer.create({ container, url })
        instance = ws
        ws.on('ready', () => {
          if (!destroyed) onReady?.()
        })
        ws.on('error', () => {
          if (!destroyed) onError?.()
        })
      })
      .catch(() => {
        if (!destroyed) onError?.()
      })

    return {
      play() {
        if (destroyed || !instance) {
          return
        }
        void instance
          .play(startMs / 1000, endMs / 1000)
          .catch(() => onError?.())
      },
      stop() {
        if (destroyed || !instance) {
          return
        }
        instance.stop()
      },
      destroy() {
        destroyed = true
        instance?.destroy()
        instance = null
      },
    }
  },
}
