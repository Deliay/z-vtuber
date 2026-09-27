import { describe, it, expect, vi, beforeEach } from 'vitest'
import { defaultWaveformLoader, WAVEFORM_PREVIEW_MAX_MS } from './waveformLoader'

/**
 * 真实 loader（`defaultWaveformLoader`）单测：mock `wavesurfer.js`，锁定播放区间口径。
 *
 * 背景（P0 回归）：`trainingAudioClipUrl` 拼出的是**已被 ffmpeg 裁剪的片段**
 * （`AudioClipExtractor` 以 `-ss startMs -t (endMs-startMs)` 抽取，输出时间轴从 0 起、时长 = endMs-startMs），
 * 因此 wavesurfer 内部必须用**片段相对时间轴**播放；早期实现按录播绝对秒 seek，
 * 分钟级条目会 seek 到片段之外导致完全无声。
 */
const ws = vi.hoisted(() => {
  const on = vi.fn()
  const play = vi.fn(() => Promise.resolve())
  const stop = vi.fn()
  const destroy = vi.fn()
  const create = vi.fn(() => ({ on, play, stop, destroy }))
  return { on, play, stop, destroy, create }
})

vi.mock('wavesurfer.js', () => ({ default: { create: ws.create } }))

function load(options?: {
  startMs?: number
  endMs?: number
  onReady?: () => void
  onError?: () => void
}) {
  const container = document.createElement('div')
  const player = defaultWaveformLoader.load({
    container,
    url: '/api/b/live/101/recordings/a.flv/audio?startMs=600000&endMs=601500',
    startMs: options?.startMs ?? 600000,
    endMs: options?.endMs ?? 601500,
    onReady: options?.onReady,
    onError: options?.onError,
  })
  return { player, container }
}

describe('defaultWaveformLoader', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('should create wavesurfer with the container and clip url', async () => {
    const { container } = load()

    await vi.waitFor(() => expect(ws.create).toHaveBeenCalledTimes(1))
    expect(ws.create).toHaveBeenCalledWith(
      expect.objectContaining({ container, url: expect.stringContaining('startMs=600000') }),
    )
  })

  it('should play on the clip-relative timeline (0 → 片段时长)，而非录播绝对时间', async () => {
    const { player } = load({ startMs: 600000, endMs: 601500 })
    await vi.waitFor(() => expect(ws.create).toHaveBeenCalledTimes(1))

    player.play()

    // 片段 600000→601500 只有 1.5s：必须播 (0, 1.5)；若播 (600, 601.5) 则完全无声
    expect(ws.play).toHaveBeenCalledWith(0, 1.5)
  })

  it('should keep the 5 minute preview window on the clip timeline when endMs falls back', async () => {
    const startMs = 1200000
    const { player } = load({ startMs, endMs: startMs + WAVEFORM_PREVIEW_MAX_MS })
    await vi.waitFor(() => expect(ws.create).toHaveBeenCalledTimes(1))

    player.play()

    expect(WAVEFORM_PREVIEW_MAX_MS).toBe(300000)
    expect(ws.play).toHaveBeenCalledWith(0, WAVEFORM_PREVIEW_MAX_MS / 1000)
  })

  it('should return the play promise to the caller instead of swallowing rejections as load errors', async () => {
    const failure = new Error('autoplay blocked')
    const onError = vi.fn()
    const { player } = load({ onError })
    await vi.waitFor(() => expect(ws.create).toHaveBeenCalledTimes(1))

    // 真实 wavesurfer 的 play 返回 Promise；失败（如 autoplay 拦截）须交给调用方处理
    ws.play.mockImplementationOnce(() => Promise.reject(failure))

    const result = player.play()
    expect(result).toBeInstanceOf(Promise)
    await expect(result as Promise<void>).rejects.toBe(failure)

    // 播放失败不得路由成「加载失败」，两者语义不串
    expect(onError).not.toHaveBeenCalled()
  })

  it('should resolve the play promise on a successful play', async () => {
    const { player } = load()
    await vi.waitFor(() => expect(ws.create).toHaveBeenCalledTimes(1))

    const result = player.play()
    expect(result).toBeInstanceOf(Promise)
    await expect(result as Promise<void>).resolves.toBeUndefined()
  })

  it('should stop playback on demand', async () => {
    const { player } = load()
    await vi.waitFor(() => expect(ws.create).toHaveBeenCalledTimes(1))

    player.stop()

    expect(ws.stop).toHaveBeenCalledTimes(1)
  })

  it('should destroy the wavesurfer instance on destroy', async () => {
    const { player } = load()
    await vi.waitFor(() => expect(ws.create).toHaveBeenCalledTimes(1))

    player.destroy()

    expect(ws.destroy).toHaveBeenCalledTimes(1)

    // 销毁后不再响应播放/停止
    player.play()
    expect(ws.play).not.toHaveBeenCalled()
  })
})
