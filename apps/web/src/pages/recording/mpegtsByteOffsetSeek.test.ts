import { describe, it, expect, vi, afterEach } from 'vitest'
import {
  ByteOffsetSeekUnsupportedError,
  LAZY_LOAD_GUARD_TIMEOUT_MS,
  applyBufferLimits,
  applyByteOffsetSeek,
  attachByteOffsetSeek,
  disposeByteOffsetSeek,
  getLazyLoadGuardDepth,
  hasByteOffsetSeekSupport,
  releaseLazyLoadGuard,
  type MpegtsSeekablePlayer,
} from './mpegtsByteOffsetSeek'

interface FakePlayerResult {
  player: MpegtsSeekablePlayer
  calls: string[]
  listeners: Map<string, () => void>
  transmuxerListeners: Map<string, () => void>
  config: { lazyLoad?: boolean; [key: string]: unknown }
}

function createFakePlayer(options: { media?: unknown } = {}): FakePlayerResult {
  const calls: string[] = []
  const listeners = new Map<string, () => void>()
  const transmuxerListeners = new Map<string, () => void>()
  const config: { lazyLoad?: boolean; [key: string]: unknown } = { lazyLoad: true }

  const player = {
    _player_engine: {
      _mse_controller: {
        flush: () => calls.push('flush'),
      },
      _loading_controller: {
        resumeTransmuxer: () => calls.push('resumeTransmuxer'),
      },
      _seeking_handler: {
        directSeek: (seconds: number) => calls.push(`directSeek:${seconds}`),
      },
      _config: config,
      _media_element: options.media,
      _transmuxer: {
        on: (event: string, listener: () => void) => {
          transmuxerListeners.set(event, listener)
        },
        off: (event: string) => {
          transmuxerListeners.delete(event)
        },
        _internalAbort: () => calls.push('internalAbort'),
        _remuxer: {
          seek: (milliseconds?: number) => calls.push(`remuxer.seek:${milliseconds}`),
          insertDiscontinuity: () => calls.push('insertDiscontinuity'),
        },
        _loadSegment: (index: number, fileposition?: number) =>
          calls.push(`loadSegment:${index}:${fileposition}`),
      },
    },
    on: (event: string, listener: () => void) => {
      listeners.set(event, listener)
    },
    off: (event: string) => {
      listeners.delete(event)
    },
  } as unknown as MpegtsSeekablePlayer

  return { player, calls, listeners, transmuxerListeners, config }
}

describe('mpegtsByteOffsetSeek', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('should run bridge steps in the patched order', () => {
    const { player, calls, config } = createFakePlayer()

    applyByteOffsetSeek(player, 12_345, 42_000)

    expect(calls).toEqual([
      'flush',
      'internalAbort',
      'remuxer.seek:42000',
      'insertDiscontinuity',
      'loadSegment:0:12345',
      'resumeTransmuxer',
      'directSeek:42',
    ])
    expect(config.lazyLoad).toBe(false)
    expect(getLazyLoadGuardDepth(player)).toBe(1)

    releaseLazyLoadGuard(player)
    expect(config.lazyLoad).toBe(true)
  })

  it('should keep lazyLoad disabled during re-entrant seeks and restore once at depth 0', () => {
    const { player, config, transmuxerListeners } = createFakePlayer()

    applyByteOffsetSeek(player, 100, 1000)
    applyByteOffsetSeek(player, 200, 2000)
    expect(getLazyLoadGuardDepth(player)).toBe(2)
    expect(config.lazyLoad).toBe(false)

    transmuxerListeners.get('media_segment')?.()
    expect(getLazyLoadGuardDepth(player)).toBe(1)
    expect(config.lazyLoad).toBe(false)

    transmuxerListeners.get('recommend_seekpoint')?.()
    expect(getLazyLoadGuardDepth(player)).toBe(0)
    expect(config.lazyLoad).toBe(true)
  })

  it('should restore lazyLoad after the 3s guard timeout', () => {
    vi.useFakeTimers()
    const { player, config } = createFakePlayer()

    applyByteOffsetSeek(player, 100, 1000)
    expect(config.lazyLoad).toBe(false)

    vi.advanceTimersByTime(LAZY_LOAD_GUARD_TIMEOUT_MS)

    expect(getLazyLoadGuardDepth(player)).toBe(0)
    expect(config.lazyLoad).toBe(true)
  })

  it('should clamp currentTime to the covering buffered range after seek', () => {
    vi.useFakeTimers()
    const media = {
      currentTime: 10,
      buffered: {
        length: 1,
        start: () => 50,
        end: () => 60,
      },
    }
    const { player } = createFakePlayer({ media })

    applyByteOffsetSeek(player, 1_000, 55_000)
    vi.advanceTimersByTime(300)

    expect((media as { currentTime: number }).currentTime).toBe(50)
  })

  it('should return false and throw explicitly when internal structure is missing', () => {
    const player = {} as MpegtsSeekablePlayer

    expect(hasByteOffsetSeekSupport(player)).toBe(false)
    expect(attachByteOffsetSeek(player)).toBe(false)
    expect(() => applyByteOffsetSeek(player, 0, 0)).toThrow(ByteOffsetSeekUnsupportedError)
    expect(() => applyBufferLimits(player, { lazyLoadMaxDuration: 1 })).toThrow(
      ByteOffsetSeekUnsupportedError,
    )
  })

  it('should attach before load (transmuxer not created yet) and fail explicitly on seek', () => {
    const player = {
      _player_engine: { _config: { lazyLoad: true } },
    } as unknown as MpegtsSeekablePlayer

    expect(hasByteOffsetSeekSupport(player)).toBe(false)
    expect(attachByteOffsetSeek(player)).toBe(true)
    expect(() => player.seekToByteOffset?.(100, 1000)).toThrow(ByteOffsetSeekUnsupportedError)
  })

  it('should attach and merge buffer limits without dropping existing config', () => {
    const { player, config } = createFakePlayer()
    config.lazyLoadRecoverDuration = 30

    expect(attachByteOffsetSeek(player)).toBe(true)
    expect(attachByteOffsetSeek(player)).toBe(true)

    player.setBufferLimits?.({
      lazyLoadMaxDuration: 5,
      lazyLoadRecoverDuration: Number.NaN,
      autoCleanupMaxBackwardDuration: 4,
    })

    expect(config.lazyLoadMaxDuration).toBe(5)
    expect(config.lazyLoadRecoverDuration).toBe(30)
    expect(config.autoCleanupMaxBackwardDuration).toBe(4)
  })

  it('should dispose guard timers idempotently', () => {
    const { player, config } = createFakePlayer()

    applyByteOffsetSeek(player, 100, 1000)
    disposeByteOffsetSeek(player)
    disposeByteOffsetSeek(player)

    expect(config.lazyLoad).toBe(true)
    expect(getLazyLoadGuardDepth(player)).toBe(0)
  })
})
