import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  BUFFER_BUDGET_BYTES,
  DEFAULT_BITRATE_BPS,
  computeBufferConfig,
  configureBufferForBitrate,
  currentPlaybackUrl,
  destroyPlayer,
  isCurrentSeekSupported,
  isPlaybackSupported,
  playRecording,
  probeSeekable,
  seekToByteOffset,
} from './recordingPlayer'

const mpegtsMock = vi.hoisted(() => ({
  createPlayer: vi.fn(),
  isSupported: vi.fn(() => true),
}))

vi.mock('mpegts.js', () => ({ default: mpegtsMock }))

interface FakeMpegtsPlayer {
  destroy: ReturnType<typeof vi.fn>
  attachMediaElement: ReturnType<typeof vi.fn>
  load: ReturnType<typeof vi.fn>
  play: ReturnType<typeof vi.fn>
  pause: ReturnType<typeof vi.fn>
  _player_engine: {
    _mse_controller: { flush: ReturnType<typeof vi.fn> }
    _loading_controller: { resumeTransmuxer: ReturnType<typeof vi.fn> }
    _seeking_handler: { directSeek: ReturnType<typeof vi.fn> }
    _config: {
      lazyLoad?: boolean
      lazyLoadMaxDuration?: number
      autoCleanupMaxBackwardDuration?: number
      [key: string]: number | boolean | undefined
    }
    _transmuxer: {
      _internalAbort: ReturnType<typeof vi.fn>
      _remuxer: { seek: ReturnType<typeof vi.fn>; insertDiscontinuity: ReturnType<typeof vi.fn> }
      _loadSegment: ReturnType<typeof vi.fn>
    }
  }
}

function createFakeMpegtsPlayer(): FakeMpegtsPlayer {
  return {
    destroy: vi.fn(),
    attachMediaElement: vi.fn(),
    load: vi.fn(),
    play: vi.fn(async () => undefined),
    pause: vi.fn(),
    _player_engine: {
      _mse_controller: { flush: vi.fn() },
      _loading_controller: { resumeTransmuxer: vi.fn() },
      _seeking_handler: { directSeek: vi.fn() },
      _config: { lazyLoad: true },
      _transmuxer: {
        _internalAbort: vi.fn(),
        _remuxer: { seek: vi.fn(), insertDiscontinuity: vi.fn() },
        _loadSegment: vi.fn(),
      },
    },
  }
}

function createVideo(): HTMLVideoElement {
  return document.createElement('video')
}

describe('recordingPlayer', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mpegtsMock.isSupported.mockReturnValue(true)
    destroyPlayer()
  })

  afterEach(() => {
    destroyPlayer()
  })

  describe('computeBufferConfig', () => {
    it('should split the 50MB budget into 60% forward and 40% backward', () => {
      const config = computeBufferConfig(50 * 1024 * 1024, 100)

      expect(config.lazyLoad).toBe(true)
      expect(config.lazyLoadMaxDuration).toBeCloseTo(60, 5)
      expect(config.autoCleanupMaxBackwardDuration).toBeCloseTo(40, 5)
      expect(config.autoCleanupMinBackwardDuration).toBeCloseTo(20, 5)
      expect(((config.lazyLoadMaxDuration + config.autoCleanupMaxBackwardDuration) * 4_000_000) / 8)
        .toBeLessThanOrEqual(BUFFER_BUDGET_BYTES + 1)
    })

    it('should use 50Mbps fallback when duration is unknown', () => {
      const config = computeBufferConfig(100 * 1024 * 1024, 0)

      expect(config.lazyLoadMaxDuration).toBeCloseTo(
        ((BUFFER_BUDGET_BYTES * 8) / DEFAULT_BITRATE_BPS) * 0.6,
        5,
      )
      expect(config.autoCleanupMaxBackwardDuration).toBeCloseTo(
        ((BUFFER_BUDGET_BYTES * 8) / DEFAULT_BITRATE_BPS) * 0.4,
        5,
      )
    })

    it('should clamp seconds to [0.1, 300]', () => {
      const slow = computeBufferConfig(1000, 1000)
      expect(slow.lazyLoadMaxDuration).toBe(300)
      expect(slow.autoCleanupMaxBackwardDuration).toBe(300)

      const fast = computeBufferConfig(1024 * 1024 * 1024, 1)
      expect(fast.lazyLoadMaxDuration).toBeLessThanOrEqual(300)
      expect(fast.autoCleanupMaxBackwardDuration).toBeLessThanOrEqual(300)
    })

    it('should always keep (forward + backward) within the byte budget', () => {
      for (const [fileSize, duration] of [
        [1024 * 1024 * 1024, 1],
        [100 * 1024 * 1024, 100],
        [10 * 1024 * 1024 * 1024, 10_000],
      ]) {
        const config = computeBufferConfig(fileSize, duration)
        const bitrate = (fileSize * 8) / duration
        expect(
          ((config.lazyLoadMaxDuration + config.autoCleanupMaxBackwardDuration) * bitrate) / 8,
        ).toBeLessThanOrEqual(BUFFER_BUDGET_BYTES + 1)
      }
    })
  })

  describe('isPlaybackSupported', () => {
    it('should return false without MSE and true when H.264/AAC is supported', () => {
      const original = Object.getOwnPropertyDescriptor(window, 'MediaSource')
      Object.defineProperty(window, 'MediaSource', { configurable: true, value: undefined })
      expect(isPlaybackSupported()).toBe(false)

      const isTypeSupported = vi.fn(() => true)
      Object.defineProperty(window, 'MediaSource', {
        configurable: true,
        value: { isTypeSupported },
      })
      expect(isPlaybackSupported()).toBe(true)
      expect(isTypeSupported).toHaveBeenCalledWith('video/mp4; codecs="avc1.42E01E,mp4a.40.2"')

      if (original) {
        Object.defineProperty(window, 'MediaSource', original)
      } else {
        delete (window as { MediaSource?: unknown }).MediaSource
      }
    })
  })

  describe('probeSeekable', () => {
    it('should accept 206 responses', async () => {
      const cancel = vi.fn(async () => undefined)
      const fetchImpl = vi.fn(async () => ({
        status: 206,
        headers: new Headers(),
        body: { cancel },
      })) as unknown as typeof fetch

      await expect(probeSeekable('/api/a', fetchImpl)).resolves.toBe(true)
      expect(cancel).toHaveBeenCalled()
    })

    it('should accept accept-ranges: bytes and reject others', async () => {
      const bytesFetch = vi.fn(async () => ({
        status: 200,
        headers: new Headers({ 'accept-ranges': 'bytes' }),
        body: null,
      })) as unknown as typeof fetch
      await expect(probeSeekable('/api/a', bytesFetch)).resolves.toBe(true)

      const noneFetch = vi.fn(async () => ({
        status: 200,
        headers: new Headers(),
        body: null,
      })) as unknown as typeof fetch
      await expect(probeSeekable('/api/a', noneFetch)).resolves.toBe(false)
    })

    it('should return false on network error', async () => {
      const fetchImpl = vi.fn(async () => {
        throw new Error('network')
      }) as unknown as typeof fetch

      await expect(probeSeekable('/api/a', fetchImpl)).resolves.toBe(false)
    })
  })

  describe('playback singleton', () => {
    it('should create, attach and play a single player and destroy the previous one', async () => {
      const first = createFakeMpegtsPlayer()
      const second = createFakeMpegtsPlayer()
      mpegtsMock.createPlayer
        .mockImplementationOnce(() => first)
        .mockImplementationOnce(() => second)

      const video = createVideo()
      await expect(
        playRecording({ url: '/api/first.flv', video, fileSize: 1024 }),
      ).resolves.toBe(true)
      expect(first.attachMediaElement).toHaveBeenCalledWith(video)
      expect(first.load).toHaveBeenCalled()
      expect(currentPlaybackUrl()).toBe('/api/first.flv')
      expect(isCurrentSeekSupported()).toBe(true)

      await playRecording({ url: '/api/second.flv', video, fileSize: 2048 })
      expect(first.destroy).toHaveBeenCalled()
      expect(currentPlaybackUrl()).toBe('/api/second.flv')
      expect(mpegtsMock.createPlayer).toHaveBeenLastCalledWith(
        expect.objectContaining({ type: 'flv', isLive: false, url: '/api/second.flv' }),
        expect.objectContaining({ seekType: 'range' }),
      )
    })

    it('should return false when mpegts internal structure is missing', async () => {
      const bare = {
        destroy: vi.fn(),
        attachMediaElement: vi.fn(),
        load: vi.fn(),
        play: vi.fn(async () => undefined),
        pause: vi.fn(),
      }
      mpegtsMock.createPlayer.mockReturnValueOnce(bare)

      await expect(
        playRecording({ url: '/api/a.flv', video: createVideo(), fileSize: 1024 }),
      ).resolves.toBe(false)
      expect(isCurrentSeekSupported()).toBe(false)
      expect(seekToByteOffset(100, 1000)).toBe(false)
    })

    it('should return false when mpegts reports no MSE support', async () => {
      mpegtsMock.isSupported.mockReturnValue(false)

      await expect(
        playRecording({ url: '/api/a.flv', video: createVideo(), fileSize: 1024 }),
      ).resolves.toBe(false)
      expect(mpegtsMock.createPlayer).not.toHaveBeenCalled()
    })

    it('should seek by byte offset and reconfigure buffer limits', async () => {
      const player = createFakeMpegtsPlayer()
      mpegtsMock.createPlayer.mockReturnValueOnce(player)

      await playRecording({ url: '/api/a.flv', video: createVideo(), fileSize: 1000 })

      expect(seekToByteOffset(1234, 5000)).toBe(true)
      expect(player._player_engine._transmuxer._loadSegment).toHaveBeenCalledWith(0, 1234)

      configureBufferForBitrate(50 * 1024 * 1024, 100)
      expect(player._player_engine._config.lazyLoadMaxDuration).toBeCloseTo(60, 5)
      expect(player._player_engine._config.autoCleanupMaxBackwardDuration).toBeCloseTo(40, 5)
    })

    it('should only destroy the player matching the given url', async () => {
      const player = createFakeMpegtsPlayer()
      mpegtsMock.createPlayer.mockReturnValueOnce(player)

      await playRecording({ url: '/api/a.flv', video: createVideo(), fileSize: 1000 })
      destroyPlayer('/api/other.flv')
      expect(player.destroy).not.toHaveBeenCalled()
      expect(currentPlaybackUrl()).toBe('/api/a.flv')

      destroyPlayer('/api/a.flv')
      expect(player.destroy).toHaveBeenCalled()
      expect(currentPlaybackUrl()).toBeNull()
    })
  })
})
