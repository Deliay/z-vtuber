/**
 * 录播预览播放器封装：mpegts.js（MSE）直读既有录播下载端点（Range 已透传），
 * 单例语义（任何时刻至多一路播放）+ 加载代次防竞态 + 字节预算缓冲 + 字节偏移续播桥接。
 *
 * mpegts 只在浏览器运行时动态 import（避免 jsdom/SSR 求值浏览器全局）。
 */
import type Mpegts from 'mpegts.js'
import {
  attachByteOffsetSeek,
  disposeByteOffsetSeek,
  type MpegtsSeekablePlayer,
} from './mpegtsByteOffsetSeek'

/** 单个播放器允许占用的 MSE 缓冲预算（字节）。 */
export const BUFFER_BUDGET_BYTES = 50 * 1024 * 1024

/** duration 未知时的保守码率兜底（50Mbps），避免默认 180s 前瞻击穿 MSE 配额。 */
export const DEFAULT_BITRATE_BPS = 50_000_000

export const MIN_BUFFER_SECONDS = 0.1
export const MAX_BUFFER_SECONDS = 300

const FORWARD_BUDGET_RATIO = 0.6
const BACKWARD_BUDGET_RATIO = 0.4

export interface RecordingBufferConfig {
  lazyLoad: boolean
  lazyLoadMaxDuration: number
  lazyLoadRecoverDuration: number
  autoCleanupMaxBackwardDuration: number
  autoCleanupMinBackwardDuration: number
}

type MpegtsPlayer = Mpegts.Player & MpegtsSeekablePlayer

interface CurrentPlayback {
  player: MpegtsPlayer
  url: string
  seekSupported: boolean
}

let current: CurrentPlayback | null = null
let playbackGeneration = 0
let mpegtsModule: typeof Mpegts | null = null

function clampSeconds(value: number): number {
  return Math.min(MAX_BUFFER_SECONDS, Math.max(MIN_BUFFER_SECONDS, value))
}

/**
 * 按文件大小/时长估算码率，把 50MB 缓冲预算折算为 mpegts 的前瞻/回退秒数：
 * 前瞻 60% / 回退 40%；duration 未知按 50Mbps 兜底；秒数钳制 [0.1, 300]。
 * 极端高码率下再按预算整体缩放，保证 (前瞻+回退) × 码率 ≤ 预算。
 */
export function computeBufferConfig(
  fileSize: number,
  durationSeconds: number,
): RecordingBufferConfig {
  const bitrateBps =
    Number.isFinite(fileSize) &&
    fileSize > 0 &&
    Number.isFinite(durationSeconds) &&
    durationSeconds > 0
      ? (fileSize * 8) / durationSeconds
      : DEFAULT_BITRATE_BPS

  const totalSeconds = (BUFFER_BUDGET_BYTES * 8) / bitrateBps
  let forward = clampSeconds(totalSeconds * FORWARD_BUDGET_RATIO)
  let backward = clampSeconds(totalSeconds * BACKWARD_BUDGET_RATIO)

  const usedBytes = ((forward + backward) * bitrateBps) / 8
  if (usedBytes > BUFFER_BUDGET_BYTES) {
    const scale = BUFFER_BUDGET_BYTES / usedBytes
    forward *= scale
    backward *= scale
  }

  return {
    lazyLoad: true,
    lazyLoadMaxDuration: forward,
    lazyLoadRecoverDuration: Math.max(forward, 1),
    autoCleanupMaxBackwardDuration: backward,
    autoCleanupMinBackwardDuration: Math.max(backward / 2, 0.05),
  }
}

/**
 * 浏览器是否具备在线预览能力（MSE + H.264/AAC）。
 * 与 mpegts.isSupported() 同口径，但不触发模块求值，便于页面入口在挂载前门控。
 */
export function isPlaybackSupported(): boolean {
  if (typeof window === 'undefined') {
    return false
  }

  const mediaSource = (
    window as Window & {
      MediaSource?: typeof MediaSource
      ManagedMediaSource?: typeof MediaSource
    }
  ).MediaSource ??
    (
      window as Window & {
        ManagedMediaSource?: typeof MediaSource
      }
    ).ManagedMediaSource

  if (!mediaSource) {
    return false
  }

  if (typeof mediaSource.isTypeSupported !== 'function') {
    return true
  }

  return (
    mediaSource.isTypeSupported('video/mp4; codecs="avc1.42E01E,mp4a.40.2"') ||
    mediaSource.isTypeSupported('video/mp4; codecs="avc1.42E01E"')
  )
}

/**
 * 探测下载端点是否支持 Range：`Range: bytes=0-0` 返回 206，或响应声明 accept-ranges: bytes。
 * 探测失败禁用拖动但保留顺序播放，绝不整文件 buffer。
 */
export async function probeSeekable(
  url: string,
  fetchImpl: typeof fetch = fetch,
): Promise<boolean> {
  try {
    const response = await fetchImpl(url, {
      method: 'GET',
      headers: { Range: 'bytes=0-0' },
    })

    const acceptRanges = response.headers.get('accept-ranges')?.toLowerCase() ?? ''
    const seekable = response.status === 206 || acceptRanges.includes('bytes')

    try {
      await response.body?.cancel()
    } catch {
      // 响应体已关闭：忽略
    }

    return seekable
  } catch {
    return false
  }
}

async function loadMpegts(): Promise<typeof Mpegts> {
  if (!mpegtsModule) {
    const module = await import('mpegts.js')
    mpegtsModule = module.default
  }
  return mpegtsModule
}

function disposeCurrent(): void {
  if (!current) {
    return
  }

  disposeByteOffsetSeek(current.player)
  try {
    current.player.destroy()
  } catch {
    // 播放器已销毁/未初始化：忽略
  }
  current = null
}

export interface PlayRecordingParams {
  url: string
  video: HTMLVideoElement
  fileSize: number
}

/**
 * 打开录播预览并立即播放。返回是否支持未缓冲 seek（桥接 attach 成功）。
 * 单例+代次：加载期间被新的 play/destroy 取代时丢弃本次实例。
 */
export async function playRecording({
  url,
  video,
  fileSize,
}: PlayRecordingParams): Promise<boolean> {
  const generation = ++playbackGeneration
  disposeCurrent()

  const mpegts = await loadMpegts()
  if (generation !== playbackGeneration) {
    return false
  }

  const supported = typeof mpegts.isSupported === 'function' ? mpegts.isSupported() : true
  if (!supported) {
    return false
  }

  // 首播即传入字节预算缓冲（默认 180s 前瞻会击穿 MSE 配额）；metadata 到手后由 UI 重算
  const bufferConfig = computeBufferConfig(fileSize, 0)
  const player = mpegts.createPlayer(
    {
      type: 'flv',
      isLive: false,
      url,
      ...(Number.isFinite(fileSize) && fileSize > 0 ? { filesize: fileSize } : {}),
    },
    {
      seekType: 'range',
      enableWorker: false,
      enableWorkerForMSE: false,
      ...bufferConfig,
    },
  ) as MpegtsPlayer

  const seekSupported = attachByteOffsetSeek(player)
  player.attachMediaElement(video)
  player.load()

  if (generation !== playbackGeneration) {
    disposeByteOffsetSeek(player)
    player.destroy()
    return false
  }

  current = { player, url, seekSupported }

  try {
    await player.play()
  } catch {
    // 自动播放被浏览器拦截：保留播放器，由 UI 的播放按钮重试
  }

  return seekSupported
}

/** 按归属销毁单例播放器；url 省略时销毁任意当前实例。 */
export function destroyPlayer(url?: string): void {
  if (url && current && current.url !== url) {
    return
  }

  playbackGeneration += 1
  disposeCurrent()
}

export function pausePlayer(): void {
  current?.player.pause()
}

export async function resumePlayer(): Promise<void> {
  if (!current) {
    return
  }

  try {
    await current.player.play()
  } catch {
    // 播放被拦截/中断：由 UI 提示
  }
}

/** 从字节偏移续播；未 attach 桥接或播放器缺失返回 false（UI 显性降级）。 */
export function seekToByteOffset(fileposition: number, milliseconds: number): boolean {
  const player = current?.player
  if (!player || typeof player.seekToByteOffset !== 'function') {
    return false
  }

  try {
    player.seekToByteOffset(fileposition, milliseconds)
    return true
  } catch {
    return false
  }
}

/** metadata 到手后用真实时长重算缓冲预算。 */
export function configureBufferForBitrate(fileSize: number, durationSeconds: number): void {
  const player = current?.player
  if (!player || typeof player.setBufferLimits !== 'function') {
    return
  }

  const config = computeBufferConfig(fileSize, durationSeconds)
  try {
    player.setBufferLimits({
      lazyLoadMaxDuration: config.lazyLoadMaxDuration,
      lazyLoadRecoverDuration: config.lazyLoadRecoverDuration,
      autoCleanupMaxBackwardDuration: config.autoCleanupMaxBackwardDuration,
      autoCleanupMinBackwardDuration: config.autoCleanupMinBackwardDuration,
    })
  } catch {
    // 结构不符：保持首播配置，不影响播放
  }
}

export function currentPlaybackUrl(): string | null {
  return current?.url ?? null
}

export function isCurrentSeekSupported(): boolean {
  return current?.seekSupported ?? false
}
