import type WaveSurfer from 'wavesurfer.js'

/** 试听预览上限：`endMs` 为空时最多加载 `startMs` 之后的 5 分钟。 */
export const WAVEFORM_PREVIEW_MAX_MS = 300000

export interface WaveformLoadOptions {
  /** 波形容器（组件在挂载时提供）。 */
  container: HTMLElement
  /** 区间试听地址（`trainingAudioClipUrl`）。 */
  url: string
  /**
   * 录播绝对开始时间（毫秒）：与 `endMs` 一起拼 url 并推导**片段时长**。
   * 注意 `url` 指向的是已被裁剪的片段，其播放时间轴从 0 起，不等于该绝对时间。
   */
  startMs: number
  /** 录播绝对结束时间（毫秒）；片段时长 = `endMs - startMs`。 */
  endMs: number
  /** 音频加载完成、可播放。 */
  onReady?: () => void
  /** 音频加载失败（提示「音频不可用」，不阻断列表操作）。 */
  onError?: () => void
}

export interface WaveformPlayer {
  /**
   * 播放整个片段：在**片段相对时间轴**上从 0 播到 `endMs - startMs`。
   * 片段由 `/audio?startMs&endMs` 服务端裁剪（ffmpeg `-ss -t`），输出时间轴从 0 起；
   * 若按录播绝对秒 seek 会落到片段之外——分钟级条目将完全无声。
   */
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
        // 片段相对时间轴：url 已按 [startMs, endMs) 服务端裁剪，播放从 0 到片段时长；
        // 传录播绝对秒（如 600s）会 seek 到片段之外导致无声。
        const durationMs = Math.max(0, endMs - startMs)
        void instance
          .play(0, durationMs > 0 ? durationMs / 1000 : undefined)
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
