/**
 * mpegts.js 字节偏移续播桥接（vendored，移植 Deliay/sumire `mpegts.js@1.8.2` 补丁的 TS 版本）。
 *
 * 背景：录播 FLV 的 onMetaData 不含 keyframes 索引 → `MediaInfo.isSeekable() === false`
 * → `TransmuxingController.seek()` 静默 no-op，未缓冲 seek 会永久冻结。
 *
 * 本模块在 `mpegts.createPlayer()` 返回的实例上显式附加：
 * - `seekToByteOffset(fileposition, milliseconds)`：flush MSE → abort 旧 IO → remuxer discontinuity
 *   → 从字节偏移重新加载 segment → 对齐时间轴；
 * - `setBufferLimits(limits)`：运行时调整 lazyLoad/自动回收缓冲秒数；
 * - lazyLoad guard：seek 期间临时关闭 lazyLoad，首个媒体段/推荐 seekpoint 或 3s 超时恢复，
 *   防止续播首段为音频时 lazyLoad 暂停后永不恢复的死锁。
 *
 * 精确锁定 mpegts.js 1.8.2：这里访问的是包内部字段，升级前必须重新核对并跑桥接单测。
 * 内部结构不符时显性抛错/返回 false，由 UI 提示「预览暂不可用」，绝不静默冻结。
 */

export const LAZY_LOAD_GUARD_TIMEOUT_MS = 3000
export const SEEK_ALIGN_DURATION_MS = 5000
export const SEEK_ALIGN_INTERVAL_MS = 250

const MEDIA_SEGMENT_EVENT = 'media_segment'
const RECOMMEND_SEEKPOINT_EVENT = 'recommend_seekpoint'

export interface MpegtsBufferLimits {
  lazyLoadMaxDuration?: number
  lazyLoadRecoverDuration?: number
  autoCleanupMaxBackwardDuration?: number
  autoCleanupMinBackwardDuration?: number
}

interface InternalConfig extends MpegtsBufferLimits {
  lazyLoad?: boolean
}

interface InternalRemuxer {
  seek: (milliseconds?: number) => void
  insertDiscontinuity: () => void
}

interface GuardEmitter {
  on?: (event: string, listener: () => void) => void
  off?: (event: string, listener: () => void) => void
}

interface InternalTransmuxer extends GuardEmitter {
  _internalAbort: () => void
  _loadSegment: (index: number, fileposition?: number) => void
  _remuxer?: InternalRemuxer
  _pendingResolveSeekPoint?: number | null
  resume?: () => void
}

interface InternalMseController {
  flush: () => void
}

interface InternalLoadingController {
  resumeTransmuxer: () => void
}

interface InternalSeekingHandler {
  directSeek: (seconds: number) => void
}

interface InternalEngine {
  _transmuxer?: InternalTransmuxer
  _mse_controller?: InternalMseController
  _loading_controller?: InternalLoadingController
  _seeking_handler?: InternalSeekingHandler
  _config?: InternalConfig
  _media_element?: HTMLMediaElement
}

export interface MpegtsSeekablePlayer {
  _player_engine?: InternalEngine
  on?: (event: string, listener: () => void) => void
  off?: (event: string, listener: () => void) => void
  seekToByteOffset?: (fileposition: number, milliseconds: number) => void
  setBufferLimits?: (limits: MpegtsBufferLimits) => void
}

/** mpegts 内部结构与本桥接不匹配（版本漂移 / worker 模式 / 未 attach）。 */
export class ByteOffsetSeekUnsupportedError extends Error {
  constructor(message = 'mpegts byte-offset seek is not supported by this player build') {
    super(message)
    this.name = 'ByteOffsetSeekUnsupportedError'
  }
}

interface GuardState {
  depth: number
  previousLazyLoad: boolean | undefined
  timer: ReturnType<typeof setTimeout> | null
  onSeekProgress: () => void
  emitter: GuardEmitter
}

const guardStates = new WeakMap<MpegtsSeekablePlayer, GuardState>()
const alignTimers = new WeakMap<MpegtsSeekablePlayer, ReturnType<typeof setInterval>>()

function hasTransitionalStructure(player: MpegtsSeekablePlayer): boolean {
  const engine = player._player_engine
  const transmuxer = engine?._transmuxer
  return Boolean(
    engine?._mse_controller?.flush &&
      engine?._loading_controller?.resumeTransmuxer &&
      engine?._seeking_handler?.directSeek &&
      engine._config &&
      transmuxer?._internalAbort &&
      transmuxer?._loadSegment &&
      transmuxer._remuxer?.seek &&
      transmuxer._remuxer?.insertDiscontinuity,
  )
}

/** 当前播放器实例是否具备字节偏移续播所需的内部结构（不修改实例）。 */
export function hasByteOffsetSeekSupport(player: MpegtsSeekablePlayer): boolean {
  return hasTransitionalStructure(player)
}

/**
 * attach 时机早于 MSE source open / load：此时 `_transmuxer` 尚未创建（mpegts 默认
 * deferLoadAfterSourceOpen=true），因此只要求 player engine/config 存在；
 * 真正调用 `seekToByteOffset` 时再由 `applyByteOffsetSeek` 严格校验内部结构。
 */
function hasAttachTarget(player: MpegtsSeekablePlayer): boolean {
  return Boolean(player._player_engine?._config)
}

function forceReleaseLazyLoadGuard(player: MpegtsSeekablePlayer): void {
  const state = guardStates.get(player)
  if (!state) {
    return
  }

  if (state.timer) {
    clearTimeout(state.timer)
  }
  state.emitter.off?.(MEDIA_SEGMENT_EVENT, state.onSeekProgress)
  state.emitter.off?.(RECOMMEND_SEEKPOINT_EVENT, state.onSeekProgress)
  const config = player._player_engine?._config
  if (config) {
    config.lazyLoad = state.previousLazyLoad
  }
  guardStates.delete(player)
}

/** 测试/销毁接缝：立即恢复 seek 前 lazyLoad 配置。 */
export function releaseLazyLoadGuard(player: MpegtsSeekablePlayer): void {
  forceReleaseLazyLoadGuard(player)
}

/** 测试接缝：当前 guard 重入深度（0 表示未处于 seek guard）。 */
export function getLazyLoadGuardDepth(player: MpegtsSeekablePlayer): number {
  return guardStates.get(player)?.depth ?? 0
}

function beginLazyLoadGuard(player: MpegtsSeekablePlayer): void {
  const config = player._player_engine?._config
  if (!config) {
    return
  }

  let state = guardStates.get(player)
  if (!state) {
    const onSeekProgress = () => endLazyLoadGuard(player)
    // media_segment/recommend_seekpoint 由 TransmuxingController 的 emitter 发出，
    // Player 实例不会二次转发；优先监听 transmuxer，测试/异常结构再回退 player。
    const transmuxer = player._player_engine?._transmuxer
    const emitter: GuardEmitter =
      transmuxer?.on && transmuxer?.off ? transmuxer : (player as GuardEmitter)
    state = {
      depth: 0,
      previousLazyLoad: config.lazyLoad,
      timer: null,
      onSeekProgress,
      emitter,
    }
    guardStates.set(player, state)
    emitter.on?.(MEDIA_SEGMENT_EVENT, onSeekProgress)
    emitter.on?.(RECOMMEND_SEEKPOINT_EVENT, onSeekProgress)
  }

  state.depth += 1
  config.lazyLoad = false

  if (state.timer) {
    clearTimeout(state.timer)
  }
  state.timer = setTimeout(() => forceReleaseLazyLoadGuard(player), LAZY_LOAD_GUARD_TIMEOUT_MS)
}

function endLazyLoadGuard(player: MpegtsSeekablePlayer): void {
  const state = guardStates.get(player)
  if (!state) {
    return
  }

  state.depth -= 1
  if (state.depth <= 0) {
    forceReleaseLazyLoadGuard(player)
  }
}

function startSeekAlignment(player: MpegtsSeekablePlayer, targetSeconds: number): void {
  const media = player._player_engine?._media_element
  if (!media || typeof media.buffered === 'undefined') {
    return
  }

  const previous = alignTimers.get(player)
  if (previous) {
    clearInterval(previous)
  }

  const startedAt = Date.now()
  const timer = setInterval(() => {
    const buffered = media.buffered
    let coveringStart: number | null = null
    for (let index = 0; index < buffered.length; index += 1) {
      if (buffered.start(index) <= targetSeconds && targetSeconds < buffered.end(index)) {
        coveringStart = buffered.start(index)
        break
      }
    }

    // DTS→PTS 偏移可能让 currentTime 落在新缓冲起点之前而永久 seeking：夹到覆盖区间起点
    if (coveringStart !== null && media.currentTime < coveringStart - 0.05) {
      media.currentTime = coveringStart
    }

    if (Date.now() - startedAt >= SEEK_ALIGN_DURATION_MS) {
      clearInterval(timer)
      if (alignTimers.get(player) === timer) {
        alignTimers.delete(player)
      }
    }
  }, SEEK_ALIGN_INTERVAL_MS)

  alignTimers.set(player, timer)
}

/**
 * 从指定字节偏移续播并立即对齐时间轴。
 *
 * 调用顺序与 sumire 补丁一致（单测锁定）：
 * flush → _internalAbort → remuxer.seek/insertDiscontinuity → _loadSegment(0, offset)
 * → _pendingResolveSeekPoint → resumeTransmuxer → directSeek。
 */
export function applyByteOffsetSeek(
  player: MpegtsSeekablePlayer,
  fileposition: number,
  milliseconds: number,
): void {
  if (!hasTransitionalStructure(player)) {
    throw new ByteOffsetSeekUnsupportedError()
  }

  if (!Number.isFinite(fileposition) || fileposition < 0) {
    throw new ByteOffsetSeekUnsupportedError(`invalid byte offset: ${fileposition}`)
  }
  if (!Number.isFinite(milliseconds) || milliseconds < 0) {
    throw new ByteOffsetSeekUnsupportedError(`invalid seek position: ${milliseconds}`)
  }

  const engine = player._player_engine!
  const transmuxer = engine._transmuxer!
  const remuxer = transmuxer._remuxer!

  beginLazyLoadGuard(player)

  engine._mse_controller!.flush()
  transmuxer._internalAbort()
  remuxer.seek(milliseconds)
  remuxer.insertDiscontinuity()
  transmuxer._loadSegment(0, fileposition)
  transmuxer._pendingResolveSeekPoint = milliseconds
  engine._loading_controller!.resumeTransmuxer()
  engine._seeking_handler!.directSeek(milliseconds / 1000)

  startSeekAlignment(player, milliseconds / 1000)
}

/** 运行时合并缓冲上限（秒）。忽略非有限/负数，保持 mpegts 配置其余字段不变。 */
export function applyBufferLimits(player: MpegtsSeekablePlayer, limits: MpegtsBufferLimits): void {
  const config = player._player_engine?._config
  if (!config) {
    throw new ByteOffsetSeekUnsupportedError('mpegts player config is not available')
  }

  const keys: Array<keyof MpegtsBufferLimits> = [
    'lazyLoadMaxDuration',
    'lazyLoadRecoverDuration',
    'autoCleanupMaxBackwardDuration',
    'autoCleanupMinBackwardDuration',
  ]

  for (const key of keys) {
    const value = limits[key]
    if (typeof value === 'number' && Number.isFinite(value) && value >= 0) {
      config[key] = value
    }
  }
}

/**
 * 在 `mpegts.createPlayer()` 返回的实例上附加桥接方法。
 * 内部结构不符时返回 false（不抛出），由调用方按「预览暂不可用」降级。
 */
export function attachByteOffsetSeek(player: MpegtsSeekablePlayer): boolean {
  if (typeof player.seekToByteOffset === 'function' && typeof player.setBufferLimits === 'function') {
    return true
  }

  if (!hasAttachTarget(player)) {
    return false
  }

  Object.defineProperty(player, 'seekToByteOffset', {
    configurable: true,
    writable: true,
    value: (fileposition: number, milliseconds: number) =>
      applyByteOffsetSeek(player, fileposition, milliseconds),
  })
  Object.defineProperty(player, 'setBufferLimits', {
    configurable: true,
    writable: true,
    value: (limits: MpegtsBufferLimits) => applyBufferLimits(player, limits),
  })

  return true
}

/** 销毁播放器前清理 guard/对齐定时器（幂等）。 */
export function disposeByteOffsetSeek(player: MpegtsSeekablePlayer): void {
  forceReleaseLazyLoadGuard(player)
  const timer = alignTimers.get(player)
  if (timer) {
    clearInterval(timer)
    alignTimers.delete(player)
  }
}
