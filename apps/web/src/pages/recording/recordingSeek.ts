/**
 * FLV 关键帧预测（移植 Deliay/sumire `features/recordings/seek.ts` 思路）：
 * 线性估算目标字节偏移 → Range 窗口探测（必须 206）→ FLV tag 三重校验并优先对齐视频关键帧 tag
 * → 返回可交给 mpegts 字节偏移桥接续播的 fileposition。
 *
 * 上游录播 FLV 的 onMetaData 不含 keyframes 索引，mpegts 原生 seek 会静默 no-op；
 * 因此续播起点必须自行扫描到合法 tag 边界（优先视频关键帧，避免 lazyLoad 暂停死锁）。
 */

/** 关键帧扫描窗口按码率折算的秒数。 */
export const KEYFRAME_SCAN_SECONDS = 10

/** Range 探测窗口下限（字节）。 */
export const SEEK_WINDOW_BYTES = 256 * 1024

/** Range 探测窗口上限（字节）。 */
export const MAX_KEYFRAME_SCAN_BYTES = 8 * 1024 * 1024

/** FLV tag 头长度。 */
const FLV_TAG_HEADER_BYTES = 11

/** FLV tag 尾 PreviousTagSize 长度。 */
const FLV_PREV_TAG_SIZE_BYTES = 4

/** FLV tag dataSize 合理上限（防御伪 tag）。 */
const MAX_FLV_TAG_DATA_BYTES = 8 * 1024 * 1024

const FLV_TAG_AUDIO = 8
const FLV_TAG_VIDEO = 9
const FLV_TAG_SCRIPT = 18

export interface FlvTagBoundary {
  /**
   * 交给 mpegts `_loadSegment(0, fileposition)` 的字节偏移：FLV tag 头起始位置。
   * demuxer 只在首次解析文件头时跳过 4 字节 PrevTagSize0，续播解析直接从 tag 头开始。
   */
  fileposition: number
  /** 绝对字节偏移对应的 FLV tag 时间戳（毫秒）。 */
  milliseconds: number
  /** 是否为视频关键帧 tag（tagType=9 且 frameType=1）。 */
  keyframe: boolean
  /** 绝对 tag 头偏移（调试/测试用）。 */
  tagOffset: number
}

export interface SeekUnbufferedParams {
  url: string
  targetSeconds: number
  durationSeconds: number
  fileSize: number
  fetchImpl?: typeof fetch
  signal?: AbortSignal
}

export interface UnbufferedSeekResult {
  fileposition: number
  milliseconds: number
  keyframe: boolean
}

let seekGeneration = 0
let activeController: AbortController | null = null

/** 线性估算：fileSize × targetMs / durationMs，结果钳制到 [0, fileSize-1]；非法输入返回 0。 */
export function estimateByteOffset(
  targetMs: number,
  durationMs: number,
  fileSize: number,
): number {
  if (
    !Number.isFinite(targetMs) ||
    !Number.isFinite(durationMs) ||
    !Number.isFinite(fileSize) ||
    targetMs <= 0 ||
    durationMs <= 0 ||
    fileSize <= 0
  ) {
    return 0
  }

  const ratio = Math.min(1, targetMs / durationMs)
  return Math.min(Math.max(0, fileSize - 1), Math.floor(fileSize * ratio))
}

/** 按码率折算扫描窗口：bitrate/8 × 10s，钳制到 [256KB, 8MB]；非法码率取下限。 */
export function computeScanBytes(bitrateBps: number): number {
  if (!Number.isFinite(bitrateBps) || bitrateBps <= 0) {
    return SEEK_WINDOW_BYTES
  }

  return Math.min(
    MAX_KEYFRAME_SCAN_BYTES,
    Math.max(SEEK_WINDOW_BYTES, Math.round((bitrateBps / 8) * KEYFRAME_SCAN_SECONDS)),
  )
}

function readUint32BE(bytes: Uint8Array, offset: number): number {
  return (
    ((bytes[offset] << 24) |
      (bytes[offset + 1] << 16) |
      (bytes[offset + 2] << 8) |
      bytes[offset + 3]) >>>
    0
  )
}

function readTagAt(
  bytes: Uint8Array,
  offset: number,
  baseOffset: number,
): FlvTagBoundary | null {
  if (offset + FLV_TAG_HEADER_BYTES + FLV_PREV_TAG_SIZE_BYTES > bytes.length) {
    return null
  }

  const tagType = bytes[offset]
  if (tagType !== FLV_TAG_AUDIO && tagType !== FLV_TAG_VIDEO && tagType !== FLV_TAG_SCRIPT) {
    return null
  }

  const dataSize =
    (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]
  if (dataSize <= 0 || dataSize > MAX_FLV_TAG_DATA_BYTES) {
    return null
  }

  const tagEnd = offset + FLV_TAG_HEADER_BYTES + dataSize
  if (tagEnd + FLV_PREV_TAG_SIZE_BYTES > bytes.length) {
    // 窗口尚未覆盖完整 tag：交由下一轮增量扫描
    return null
  }

  const prevTagSize = readUint32BE(bytes, tagEnd)
  if (prevTagSize !== FLV_TAG_HEADER_BYTES + dataSize) {
    return null
  }

  const milliseconds =
    (bytes[offset + 4] |
      (bytes[offset + 5] << 8) |
      (bytes[offset + 6] << 16) |
      (bytes[offset + 7] << 24)) >>>
    0

  const keyframe =
    tagType === FLV_TAG_VIDEO && dataSize > 0 && (bytes[offset + FLV_TAG_HEADER_BYTES] >> 4) === 1

  const tagOffset = baseOffset + offset
  return {
    // 续播 chunk 必须从 tag 头开始（demuxer 无 mid-stream resync，也不会再次跳过 PrevTagSize）
    fileposition: tagOffset,
    milliseconds,
    keyframe,
    tagOffset,
  }
}

function scanBoundary(
  bytes: Uint8Array,
  baseOffset: number,
  requireKeyframe: boolean,
): FlvTagBoundary | null {
  for (let offset = 0; offset + FLV_TAG_HEADER_BYTES + FLV_PREV_TAG_SIZE_BYTES < bytes.length; offset++) {
    const boundary = readTagAt(bytes, offset, baseOffset)
    if (!boundary) {
      continue
    }

    if (requireKeyframe && !boundary.keyframe) {
      continue
    }

    return boundary
  }

  return null
}

/** 扫描窗口内第一个视频关键帧 tag（三重校验通过）；无则返回 null。 */
export function findVideoKeyframeBoundary(
  window: Uint8Array,
  baseOffset: number,
): FlvTagBoundary | null {
  return scanBoundary(window, baseOffset, true)
}

/** 扫描窗口内第一个合法 tag 边界（任意类型）；无则返回 null。 */
export function findNextTagBoundary(
  window: Uint8Array,
  baseOffset: number,
): FlvTagBoundary | null {
  return scanBoundary(window, baseOffset, false)
}

function parseContentRange(value: string | null): { start: number; end: number } | null {
  if (!value) {
    return null
  }

  const match = /bytes\s+(\d+)-(\d+)\//i.exec(value)
  if (!match) {
    return null
  }

  const start = Number(match[1])
  const end = Number(match[2])
  if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || end < start) {
    return null
  }

  return { start, end }
}

async function cancelBody(response: Response): Promise<void> {
  try {
    await response.body?.cancel()
  } catch {
    // 响应体已关闭/不可取消：忽略
  }
}

function concatBytes(
  left: Uint8Array<ArrayBufferLike>,
  right: Uint8Array<ArrayBufferLike>,
): Uint8Array<ArrayBufferLike> {
  if (left.length === 0) {
    return right
  }
  const merged = new Uint8Array(left.length + right.length)
  merged.set(left, 0)
  merged.set(right, left.length)
  return merged
}

/**
 * 取消当前在途探测（播放器关闭/切源时调用），使过期结果被丢弃。
 * 不改变下一次 seek 的代次语义。
 */
export function cancelPendingSeek(): void {
  seekGeneration++
  activeController?.abort()
  activeController = null
}

/**
 * 未缓冲 seek：Range 窗口探测 + FLV tag 扫描，返回续播落点。
 *
 * - 非 206（上游忽略 Range）一律取消响应体并返回 null，绝不整文件 buffer；
 * - 并发调用 last-wins：旧调用被 abort，过期结果返回 null；
 * - 窗口内无关键帧时回退任意合法 tag；完全无 tag 时按估算偏移直试（由调用方显性提示失败）。
 */
export async function seekUnbuffered(
  params: SeekUnbufferedParams,
): Promise<UnbufferedSeekResult | null> {
  const {
    url,
    targetSeconds,
    durationSeconds,
    fileSize,
    fetchImpl = fetch,
    signal,
  } = params

  if (
    !url ||
    !Number.isFinite(targetSeconds) ||
    !Number.isFinite(durationSeconds) ||
    !Number.isFinite(fileSize) ||
    targetSeconds < 0 ||
    durationSeconds <= 0 ||
    fileSize <= 0
  ) {
    return null
  }

  const generation = ++seekGeneration
  const targetMs = Math.min(durationSeconds * 1000, targetSeconds * 1000)
  const durationMs = durationSeconds * 1000
  const estimate = estimateByteOffset(targetMs, durationMs, fileSize)
  const bitrateBps = (fileSize * 8) / durationSeconds
  const scanBytes = computeScanBytes(bitrateBps)
  const rangeStart = Math.min(estimate, Math.max(0, fileSize - 1))
  const rangeEnd = Math.min(fileSize - 1, rangeStart + scanBytes - 1)

  activeController?.abort()
  const controller = new AbortController()
  activeController = controller
  const onAbort = () => controller.abort()
  signal?.addEventListener('abort', onAbort, { once: true })

  try {
    const response = await fetchImpl(url, {
      headers: { Range: `bytes=${rangeStart}-${rangeEnd}` },
      signal: controller.signal,
    })

    if (generation !== seekGeneration) {
      await cancelBody(response)
      return null
    }

    if (response.status !== 206) {
      // 上游忽略 Range / 代理未透传：绝不整文件 buffer，显性降级
      await cancelBody(response)
      return null
    }

    const baseOffset = parseContentRange(response.headers.get('content-range'))?.start ?? rangeStart
    const reader = response.body?.getReader()
    if (!reader) {
      return null
    }

    let buffer: Uint8Array<ArrayBufferLike> = new Uint8Array(0)
    let firstAnyTag: FlvTagBoundary | null = null

    try {
      while (true) {
        const { done, value } = await reader.read()
        if (done) {
          break
        }

        if (value && value.length > 0) {
          buffer = concatBytes(buffer, value)
          firstAnyTag ??= findNextTagBoundary(buffer, baseOffset)

          const keyframe = findVideoKeyframeBoundary(buffer, baseOffset)
          if (keyframe) {
            await reader.cancel()
            return generation === seekGeneration
              ? {
                  fileposition: keyframe.fileposition,
                  milliseconds: keyframe.milliseconds,
                  keyframe: true,
                }
              : null
          }
        }

        // 防御异常响应体：窗口 + 单个 tag 余量足够后停止继续读取
        if (buffer.length > scanBytes + MAX_FLV_TAG_DATA_BYTES) {
          break
        }
      }
    } finally {
      // 命中关键帧/窗口结束时取消剩余下载（已关闭时忽略）
      await reader.cancel().catch(() => undefined)
    }

    if (generation !== seekGeneration) {
      return null
    }

    if (firstAnyTag) {
      return {
        fileposition: firstAnyTag.fileposition,
        milliseconds: firstAnyTag.milliseconds,
        keyframe: firstAnyTag.keyframe,
      }
    }

    // 窗口内无任何合法 tag：按估算偏移直试（调用方失败时显性提示，不静默冻结）
    return {
      fileposition: rangeStart,
      milliseconds: targetMs,
      keyframe: false,
    }
  } catch {
    // abort / 网络失败：返回 null 由 UI 提示「该文件暂不支持跳转」
    return null
  } finally {
    signal?.removeEventListener('abort', onAbort)
    if (activeController === controller) {
      activeController = null
    }
  }
}
