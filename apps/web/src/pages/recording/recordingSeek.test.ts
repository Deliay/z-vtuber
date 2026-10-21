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
  // FLV TagHeader：UI24 Timestamp（大端）+ UI8 TimestampExtended（高 8 位）
  bytes[4] = (timestamp >>> 16) & 0xff
  bytes[5] = (timestamp >>> 8) & 0xff
  bytes[6] = timestamp & 0xff
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

/** AVC/HEVC video tag：packetType 0=sequence header，1=NALU，2=end of sequence。 */
function packetVideoTag(
  timestamp: number,
  packetType: 0 | 1 | 2,
  options: { frameType?: 1 | 2; codecId?: 7 | 12; extra?: number[] } = {},
): Uint8Array {
  const frameType = options.frameType ?? 1
  const codecId = options.codecId ?? 7
  return makeTag({
    type: 9,
    timestamp,
    data: new Uint8Array([
      (frameType << 4) | codecId,
      packetType,
      0,
      0,
      0,
      ...(options.extra ?? []),
    ]),
  })
}

/** Enhanced header（bit7=1）：0x80 | FrameType<<4 | packetType + FourCC + payload。 */
function enhancedVideoTag(
  timestamp: number,
  fourCc: 'hvc1' | 'av01',
  packetType: 0 | 1 | 2,
): Uint8Array {
  return makeTag({
    type: 9,
    timestamp,
    data: new Uint8Array([
      0x80 | (1 << 4) | packetType,
      ...Array.from(fourCc).map((char) => char.charCodeAt(0)),
      1,
      2,
      3,
    ]),
  })
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
      // 交给 mpegts 的偏移就是 tag 头起始位置
      expect(boundary?.fileposition).toBe(boundary!.tagOffset)
    })

    it('should parse FLV 24-bit timestamp + extended byte in mpegts.js order', () => {
      // 3000ms = 0x000BB8：Tag header 字节 4..7 应为 00 0B B8 00
      const tag = videoTag(3000, 1)
      expect(Array.from(tag.subarray(4, 8))).toEqual([0x00, 0x0b, 0xb8, 0x00])

      const boundary = findVideoKeyframeBoundary(concat(new Uint8Array(4), tag), 0)
      expect(boundary?.milliseconds).toBe(3000)

      // > 24 位：0x01234567，扩展字节为最高 8 位
      const extendedTag = videoTag(0x01234567, 1)
      expect(Array.from(extendedTag.subarray(4, 8))).toEqual([0x23, 0x45, 0x67, 0x01])

      const extended = findVideoKeyframeBoundary(concat(new Uint8Array(4), extendedTag), 0)
      expect(extended?.milliseconds).toBe(0x01234567)
    })

    it('should exclude AVC sequence header and end-of-sequence tags from keyframes', () => {
      const sequenceHeader = packetVideoTag(0, 0)
      const endOfSequence = packetVideoTag(29960, 2)
      const realKeyframe = packetVideoTag(29000, 1, { extra: [9, 9] })

      const window = concat(sequenceHeader, endOfSequence, realKeyframe)
      const boundary = findVideoKeyframeBoundary(window, 0)

      expect(boundary?.milliseconds).toBe(29000)
      expect(boundary?.keyframe).toBe(true)

      // 仅 sequence header + EOS：没有任何真实关键帧
      expect(findVideoKeyframeBoundary(concat(sequenceHeader, endOfSequence), 0)).toBeNull()
    })

    it('should apply the same NALU-packet rule to enhanced HEVC/AV1 tags', () => {
      const sequenceHeader = enhancedVideoTag(0, 'hvc1', 0)
      const endOfSequence = enhancedVideoTag(29960, 'hvc1', 2)
      const realKeyframe = enhancedVideoTag(29000, 'hvc1', 1)
      const av1Keyframe = enhancedVideoTag(28000, 'av01', 1)

      const window = concat(sequenceHeader, endOfSequence, realKeyframe)
      expect(findVideoKeyframeBoundary(window, 0)?.milliseconds).toBe(29000)
      expect(findVideoKeyframeBoundary(concat(sequenceHeader, endOfSequence), 0)).toBeNull()
      expect(findVideoKeyframeBoundary(concat(av1Keyframe), 0)?.milliseconds).toBe(28000)
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
      expect(result?.fileposition).toBe(5_242_880 + 16 + audioTag(0).length)

      const [, init] = fetchImpl.mock.calls[0]
      expect((init as RequestInit).headers).toEqual({
        Range: expect.stringMatching(/^bytes=\d+-\d+$/),
      })
    })

    it('should retry an incomplete tag after more bytes arrive', async () => {
      const prefix = new Uint8Array(16)
      const audio = audioTag(100)
      const key = videoTag(101_000, 1)
      const window = concat(prefix, audio, key)
      // 第一块只到 keyframe tag header + 2 字节 data：header 完整、tag 不完整，进入 pending
      const splitAt = prefix.length + audio.length + 11 + 2
      const fetchImpl = vi.fn(async () =>
        streamResponse([window.subarray(0, splitAt), window.subarray(splitAt)]),
      )

      const result = await seekUnbuffered({ ...baseParams, fetchImpl })

      expect(result?.keyframe).toBe(true)
      expect(result?.milliseconds).toBe(101_000)
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

      expect(result?.fileposition).toBe(9000 + 10)
    })

    it('should back-probe once and pick the last real keyframe when estimate passes the tail keyframe', async () => {
      const header = new Uint8Array(16)
      const audio = audioTag(29_500)
      const endOfSequence = packetVideoTag(29_960, 2)
      // 前向窗口：只剩音频/EOS，没有真实关键帧
      const forwardWindow = concat(header, audio, endOfSequence)
      // 回退窗口：包含最后一个真实关键帧 29000ms
      const backWindow = concat(audioTag(28_000), packetVideoTag(29_000, 1, { extra: [9] }))

      const fetchImpl = vi
        .fn<typeof fetch>()
        .mockImplementationOnce(async () => streamResponse([forwardWindow], {
          contentRange: 'bytes 1437818-1440000/1462188',
        }))
        .mockImplementationOnce(async () => streamResponse([backWindow], {
          contentRange: 'bytes 1400000-1437817/1462188',
        }))

      const result = await seekUnbuffered({
        ...baseParams,
        targetSeconds: 29.5,
        durationSeconds: 30,
        fileSize: 1_462_188,
        fetchImpl,
      })

      expect(result).toEqual({
        fileposition: 1_400_000 + audioTag(0).length,
        milliseconds: 29_000,
        keyframe: true,
      })
      expect(result?.milliseconds).not.toBe(29_960)
      expect(fetchImpl).toHaveBeenCalledTimes(2)
      const backRange = (fetchImpl.mock.calls[1][1] as RequestInit).headers as Record<string, string>
      expect(backRange.Range).toMatch(/^bytes=\d+-1437817$/)
    })

    it('should degrade explicitly when neither forward nor backward window has a real keyframe', async () => {
      const noKeyframeWindow = concat(audioTag(29_500), packetVideoTag(29_960, 2))
      const fetchImpl = vi.fn(async () => streamResponse([noKeyframeWindow]))

      const result = await seekUnbuffered({
        ...baseParams,
        targetSeconds: 29.5,
        durationSeconds: 30,
        fileSize: 10_000,
        fetchImpl,
      })

      expect(result).toBeNull()
      expect(fetchImpl).toHaveBeenCalledTimes(2)
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
