import { describe, it, expect, vi, beforeEach } from 'vitest'
import {
  MAX_KEYFRAME_SCAN_BYTES,
  SEEK_WINDOW_BYTES,
  cancelPendingSeek,
  computeScanBytes,
  estimateByteOffset,
  findNextTagBoundary,
  findVideoKeyframeBoundary,
  seekUnbuffered,
} from './recordingSeek'

function writeUint32BE(bytes: Uint8Array, offset: number, value: number) {
  bytes[offset] = (value >>> 24) & 0xff
  bytes[offset + 1] = (value >>> 16) & 0xff
  bytes[offset + 2] = (value >>> 8) & 0xff
  bytes[offset + 3] = value & 0xff
}

function concat(...chunks: Uint8Array[]): Uint8Array {
  const length = chunks.reduce((total, chunk) => total + chunk.length, 0)
  const merged = new Uint8Array(length)
  let offset = 0
  for (const chunk of chunks) {
    merged.set(chunk, offset)
    offset += chunk.length
  }
  return merged
}

interface TagOptions {
  type: number
  timestamp: number
  data?: Uint8Array
}

function makeTag({ type, timestamp, data = new Uint8Array([1, 2, 3]) }: TagOptions): Uint8Array {
  const bytes = new Uint8Array(11 + data.length + 4)
  bytes[0] = type
  bytes[1] = (data.length >>> 16) & 0xff
  bytes[2] = (data.length >>> 8) & 0xff
  bytes[3] = data.length & 0xff
  bytes[4] = timestamp & 0xff
  bytes[5] = (timestamp >>> 8) & 0xff
  bytes[6] = (timestamp >>> 16) & 0xff
  bytes[7] = (timestamp >>> 24) & 0xff
  bytes.set(data, 11)
  writeUint32BE(bytes, 11 + data.length, 11 + data.length)
  return bytes
}

function videoTag(timestamp: number, frameType: 1 | 2): Uint8Array {
  return makeTag({
    type: 9,
    timestamp,
    data: new Uint8Array([(frameType << 4) | 7, 1, 0, 0, 0, 9, 9]),
  })
}

function audioTag(timestamp: number): Uint8Array {
  return makeTag({ type: 8, timestamp, data: new Uint8Array([0xaf, 0x01, 1, 2]) })
}

function streamResponse(
  chunks: Uint8Array[],
  options: {
    status?: number
    contentRange?: string
    onCancel?: () => void
  } = {},
): Response {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(chunk)
      }
      controller.close()
    },
    cancel() {
      options.onCancel?.()
    },
  })

  const headers = new Headers()
  if (options.contentRange) {
    headers.set('content-range', options.contentRange)
  }

  return new Response(body, { status: options.status ?? 206, headers })
}

const baseParams = {
  url: '/api/b/live/1/recordings/a.flv',
  targetSeconds: 100,
  durationSeconds: 1000,
  fileSize: 100 * 1024 * 1024,
}

describe('recordingSeek', () => {
  beforeEach(() => {
    cancelPendingSeek()
  })

  describe('estimateByteOffset', () => {
    it('should estimate linearly', () => {
      expect(estimateByteOffset(50_000, 100_000, 1000)).toBe(500)
      expect(estimateByteOffset(25_000, 100_000, 1001)).toBe(250)
    })

    it('should clamp to file bounds and handle invalid input', () => {
      expect(estimateByteOffset(200_000, 100_000, 1000)).toBe(999)
      expect(estimateByteOffset(-1, 100_000, 1000)).toBe(0)
      expect(estimateByteOffset(1, 0, 1000)).toBe(0)
      expect(estimateByteOffset(1, 1000, 0)).toBe(0)
      expect(estimateByteOffset(Number.NaN, 1000, 1000)).toBe(0)
    })
  })

  describe('computeScanBytes', () => {
    it('should derive ten seconds of bytes from bitrate', () => {
      // 4Mbps → 5MB
      expect(computeScanBytes(4_000_000)).toBe(5_000_000)
    })

    it('should clamp to [256KB, 8MB] and fall back for invalid bitrate', () => {
      expect(computeScanBytes(1_000)).toBe(SEEK_WINDOW_BYTES)
      expect(computeScanBytes(0)).toBe(SEEK_WINDOW_BYTES)
      expect(computeScanBytes(Number.NaN)).toBe(SEEK_WINDOW_BYTES)
      expect(computeScanBytes(1_000_000_000)).toBe(MAX_KEYFRAME_SCAN_BYTES)
    })
  })

  describe('FLV tag scanning', () => {
    it('should prefer the first video keyframe tag after garbage/audio/non-key tags', () => {
      const prefix = new Uint8Array([0xee, 0xee, 0xee, 0xee, 0xee])
      const audio = audioTag(1000)
      const nonKey = videoTag(2000, 2)
      const key = videoTag(3000, 1)
      const window = concat(prefix, audio, nonKey, key)

      const boundary = findVideoKeyframeBoundary(window, 10_000)

      expect(boundary).not.toBeNull()
      expect(boundary?.milliseconds).toBe(3000)
      expect(boundary?.keyframe).toBe(true)
      expect(boundary?.tagOffset).toBe(10_000 + prefix.length + audio.length + nonKey.length)
      // 交给 mpegts 的偏移指向 tag 头之前的 PrevTagSize
      expect(boundary?.fileposition).toBe(boundary!.tagOffset - 4)
    })

    it('should return the first valid tag of any type for findNextTagBoundary', () => {
      const audio = audioTag(1500)
      const key = videoTag(3000, 1)

      const boundary = findNextTagBoundary(concat(new Uint8Array(3), audio, key), 100)

      expect(boundary?.milliseconds).toBe(1500)
      expect(boundary?.keyframe).toBe(false)
      expect(boundary?.tagOffset).toBe(103)
    })

    it('should reject tags with invalid PrevTagSize (triple check)', () => {
      const corrupted = videoTag(1000, 1)
      writeUint32BE(corrupted, corrupted.length - 4, 999)
      const valid = videoTag(5000, 1)

      const boundary = findVideoKeyframeBoundary(concat(corrupted, valid), 0)

      expect(boundary?.milliseconds).toBe(5000)
    })

    it('should skip incomplete trailing candidates and rescan after more bytes', () => {
      const key = videoTag(7000, 1)
      const incomplete = key.subarray(0, 8)
      const window = concat(incomplete, audioTag(8000), key)

      const boundary = findVideoKeyframeBoundary(window, 0)

      expect(boundary?.milliseconds).toBe(7000)
    })

    it('should return null for random data', () => {
      const window = new Uint8Array(1024).fill(0xaa)
      expect(findVideoKeyframeBoundary(window, 0)).toBeNull()
      expect(findNextTagBoundary(window, 0)).toBeNull()
    })
  })

  describe('seekUnbuffered', () => {
    it('should range-probe, scan keyframe and return its fileposition', async () => {
      const window = concat(new Uint8Array(16), audioTag(100), videoTag(101_000, 1))
      const fetchImpl = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
        streamResponse([window.subarray(0, 40), window.subarray(40)], {
          contentRange: 'bytes 5242880-5243135/104857600',
        }),
      )

      const result = await seekUnbuffered({ ...baseParams, fetchImpl })

      expect(result).not.toBeNull()
      expect(result?.keyframe).toBe(true)
      expect(result?.milliseconds).toBe(101_000)
      expect(result?.fileposition).toBe(5_242_880 + 16 + audioTag(0).length - 4)

      const [, init] = fetchImpl.mock.calls[0]
      expect((init as RequestInit).headers).toEqual({
        Range: expect.stringMatching(/^bytes=\d+-\d+$/),
      })
    })

    it('should cancel the body and degrade on non-206 responses', async () => {
      const onCancel = vi.fn()
      const fetchImpl = vi.fn(async () =>
        streamResponse([videoTag(1000, 1)], { status: 200, onCancel }),
      )

      const result = await seekUnbuffered({ ...baseParams, fetchImpl })

      expect(result).toBeNull()
      expect(onCancel).toHaveBeenCalled()
    })

    it('should use Content-Range start as the corrected base offset', async () => {
      const window = concat(new Uint8Array(10), videoTag(5000, 1))
      const fetchImpl = vi.fn(async () =>
        streamResponse([window], { contentRange: 'bytes 9000-9200/100000' }),
      )

      const result = await seekUnbuffered({ ...baseParams, fetchImpl })

      expect(result?.fileposition).toBe(9000 + 10 - 4)
    })

    it('should fall back to any valid tag when no keyframe exists in the window', async () => {
      const window = concat(audioTag(2000), videoTag(2500, 2))
      const fetchImpl = vi.fn(async () => streamResponse([window]))

      const result = await seekUnbuffered({ ...baseParams, fetchImpl })

      expect(result?.keyframe).toBe(false)
      expect(result?.milliseconds).toBe(2000)
    })

    it('should fall back to the estimated offset when no valid tag exists', async () => {
      const fetchImpl = vi.fn(async () => streamResponse([new Uint8Array(512).fill(0xaa)]))

      const result = await seekUnbuffered({
        ...baseParams,
        targetSeconds: 50,
        durationSeconds: 100,
        fileSize: 1000,
        fetchImpl,
      })

      expect(result).toEqual({
        fileposition: 500,
        milliseconds: 50_000,
        keyframe: false,
      })
    })

    it('should drop stale results (last-wins) and cancel the superseded body', async () => {
      let resolveFirst: ((response: Response) => void) | undefined
      const firstResponse = new Promise<Response>((resolve) => {
        resolveFirst = resolve
      })

      const fetchImpl = vi
        .fn<typeof fetch>()
        .mockImplementationOnce(() => firstResponse)
        .mockImplementationOnce(async () => streamResponse([videoTag(9000, 1)]))

      const first = seekUnbuffered({ ...baseParams, fetchImpl })
      const second = seekUnbuffered({ ...baseParams, fetchImpl })

      await expect(second).resolves.not.toBeNull()
      resolveFirst?.(streamResponse([videoTag(3000, 1)]))

      await expect(first).resolves.toBeNull()
    })

    it('should return null for invalid parameters or failed requests', async () => {
      const fetchImpl = vi.fn<typeof fetch>()
      expect(
        await seekUnbuffered({ ...baseParams, durationSeconds: 0, fetchImpl }),
      ).toBeNull()
      expect(
        await seekUnbuffered({ ...baseParams, fileSize: 0, fetchImpl }),
      ).toBeNull()
      expect(fetchImpl).not.toHaveBeenCalled()

      const failing = vi.fn(async () => {
        throw new Error('network down')
      })
      await expect(seekUnbuffered({ ...baseParams, fetchImpl: failing })).resolves.toBeNull()
    })
  })
})
