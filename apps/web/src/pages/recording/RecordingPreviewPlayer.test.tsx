import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { RecordingPreviewPlayer } from './RecordingPreviewPlayer'
import * as player from './recordingPlayer'
import * as seek from './recordingSeek'

vi.mock('./recordingPlayer', () => ({
  isPlaybackSupported: vi.fn(() => true),
  playRecording: vi.fn(async () => true),
  destroyPlayer: vi.fn(),
  pausePlayer: vi.fn(),
  resumePlayer: vi.fn(async () => undefined),
  probeSeekable: vi.fn(async () => true),
  probeSeekableWithStatus: vi.fn(async () => ({ seekable: true, status: 206 })),
  seekToByteOffset: vi.fn(() => true),
  configureBufferForBitrate: vi.fn(),
}))

vi.mock('./recordingSeek', () => ({
  seekUnbuffered: vi.fn(async () => null),
}))

vi.mock('../../api/client', () => ({
  recordingDownloadUrl: (roomId: number, fileName: string) =>
    `/api/b/live/${roomId}/recordings/${encodeURIComponent(fileName)}`,
  recordingClipUrl: (roomId: number, fileName: string, startMs: number, endMs: number) =>
    `/api/b/live/${roomId}/recordings/${encodeURIComponent(fileName)}/clip?startMs=${startMs}&endMs=${endMs}`,
  apiErrorMessage: (error: unknown, fallback: string) => {
    const raw = (error as { serverMessage?: string })?.serverMessage ?? ''
    return raw.includes('video clip failed') ? '片段生成失败，请重试' : fallback
  },
}))

const mockPlay = vi.mocked(player.playRecording)
const mockDestroy = vi.mocked(player.destroyPlayer)
const mockProbe = vi.mocked(player.probeSeekableWithStatus)
const mockSeekToByteOffset = vi.mocked(player.seekToByteOffset)
const mockSeekUnbuffered = vi.mocked(seek.seekUnbuffered)
const mockIsPlaybackSupported = vi.mocked(player.isPlaybackSupported)

const defaultProps = {
  open: true,
  roomId: 101,
  fileName: '[RAW][101][主播A] 2026-10-21-歌回.flv',
  fileSize: 1024 * 1024,
  onClose: vi.fn(),
}

function getVideo(): HTMLVideoElement {
  return screen.getByTestId('recording-preview-video') as HTMLVideoElement
}

function setMediaState(options: {
  duration?: number
  currentTime?: number
  buffered?: Array<[number, number]>
}) {
  const video = getVideo()
  if (options.duration !== undefined) {
    Object.defineProperty(video, 'duration', {
      configurable: true,
      value: options.duration,
    })
  }
  if (options.currentTime !== undefined) {
    let current = options.currentTime
    Object.defineProperty(video, 'currentTime', {
      configurable: true,
      get: () => current,
      set: (value: number) => {
        current = value
      },
    })
  }
  if (options.buffered) {
    const ranges = options.buffered
    Object.defineProperty(video, 'buffered', {
      configurable: true,
      value: {
        length: ranges.length,
        start: (index: number) => ranges[index][0],
        end: (index: number) => ranges[index][1],
      },
    })
  }
}

async function loadMetadata(options: Parameters<typeof setMediaState>[0] = {}) {
  setMediaState({ duration: 10, currentTime: 0, ...options })
  fireEvent.loadedMetadata(getVideo())
  await waitFor(() => expect(screen.getByText(/\/ 0:10/)).toBeInTheDocument())
}

describe('RecordingPreviewPlayer', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockIsPlaybackSupported.mockReturnValue(true)
    mockPlay.mockResolvedValue(true)
    mockProbe.mockResolvedValue({ seekable: true, status: 206 })
    mockSeekToByteOffset.mockReturnValue(true)
    mockSeekUnbuffered.mockResolvedValue(null)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('should start playback on open and destroy the player on unmount', async () => {
    const { unmount } = render(<RecordingPreviewPlayer {...defaultProps} />)

    await waitFor(() => {
      expect(mockPlay).toHaveBeenCalledWith({
        url: `/api/b/live/101/recordings/${encodeURIComponent(defaultProps.fileName)}`,
        video: getVideo(),
        fileSize: defaultProps.fileSize,
      })
    })

    unmount()
    expect(mockDestroy).toHaveBeenCalledWith(
      `/api/b/live/101/recordings/${encodeURIComponent(defaultProps.fileName)}`,
    )
  })

  it('should disable seeking and show a hint when Range probe fails', async () => {
    mockProbe.mockResolvedValue({ seekable: false, status: 200 })

    render(<RecordingPreviewPlayer {...defaultProps} />)

    expect(await screen.findByText('该文件暂不支持跳转')).toBeInTheDocument()
  })

  it('should prompt and refresh when the recording file no longer exists', async () => {
    const onFileMissing = vi.fn()
    mockProbe.mockResolvedValue({ seekable: false, status: 404 })

    render(
      <RecordingPreviewPlayer {...defaultProps} onFileMissing={onFileMissing} />,
    )

    expect(await screen.findByText('文件不存在或已被清理')).toBeInTheDocument()
    expect(onFileMissing).toHaveBeenCalled()
    expect(mockPlay).not.toHaveBeenCalled()
  })

  it('should use native currentTime when the target is buffered', async () => {
    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata({ buffered: [[0, 5]] })

    fireEvent.change(screen.getByLabelText('跳转时间'), { target: { value: '0:03' } })
    fireEvent.click(screen.getByRole('button', { name: '跳转' }))

    await waitFor(() => expect(getVideo().currentTime).toBe(3))
    expect(mockSeekUnbuffered).not.toHaveBeenCalled()
  })

  it('should fall back to byte-offset seek when the target is unbuffered', async () => {
    mockSeekUnbuffered.mockResolvedValue({
      fileposition: 1234,
      milliseconds: 8000,
      keyframe: true,
    })
    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata({ buffered: [[0, 5]] })

    fireEvent.change(screen.getByLabelText('跳转时间'), { target: { value: '0:08' } })
    fireEvent.click(screen.getByRole('button', { name: '跳转' }))

    await waitFor(() => {
      expect(mockSeekToByteOffset).toHaveBeenCalledWith(1234, 8000)
    })
  })

  it('should show a hint when unbuffered seek fails', async () => {
    mockSeekUnbuffered.mockResolvedValue(null)
    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata({ buffered: [[0, 5]] })

    fireEvent.change(screen.getByLabelText('跳转时间'), { target: { value: '0:08' } })
    fireEvent.click(screen.getByRole('button', { name: '跳转' }))

    expect(await screen.findByText('该文件暂不支持跳转')).toBeInTheDocument()
  })

  it('should record current position as start/end and support clearing', async () => {
    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata()

    setMediaState({ currentTime: 3 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为起点' }))

    setMediaState({ currentTime: 7 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为终点' }))

    expect(screen.getByText('区间：0:03 - 0:07（时长 0:04）')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '清空' }))
    expect(screen.getByText('请选择起点与终点')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /下载片段/ })).toHaveAttribute(
      'aria-disabled',
      'true',
    )
  })

  it('should reject invalid manual times and start >= end', async () => {
    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata()

    fireEvent.change(screen.getByLabelText('起点时间'), { target: { value: 'abc' } })
    fireEvent.blur(screen.getByLabelText('起点时间'))
    expect(await screen.findByText('时间格式无效')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('起点时间'), { target: { value: '0:05' } })
    fireEvent.blur(screen.getByLabelText('起点时间'))
    expect(screen.getByText('区间：0:05 - 未设置')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('终点时间'), { target: { value: '0:03' } })
    fireEvent.blur(screen.getByLabelText('终点时间'))
    expect(await screen.findByText('终点必须晚于起点')).toBeInTheDocument()
  })

  it('should disable clip download until both endpoints are selected', async () => {
    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata()

    const download = screen.getByRole('button', { name: /下载片段/ })
    expect(download).toHaveAttribute('aria-disabled', 'true')

    setMediaState({ currentTime: 2 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为起点' }))
    setMediaState({ currentTime: 6 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为终点' }))

    expect(download).not.toHaveAttribute('aria-disabled')
  })

  it('should preflight the clip and trigger a download on 2xx', async () => {
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => ({
      ok: true,
      body: { cancel: vi.fn(async () => undefined) },
    }))
    vi.stubGlobal('fetch', fetchMock)

    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata()
    setMediaState({ currentTime: 2 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为起点' }))
    setMediaState({ currentTime: 6 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为终点' }))

    fireEvent.click(screen.getByRole('button', { name: /下载片段/ }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalled())
    expect(fetchMock.mock.calls[0][0]).toContain(
      '/clip?startMs=2000&endMs=6000',
    )
    await waitFor(() => expect(clickSpy).toHaveBeenCalled())
    vi.unstubAllGlobals()
  })

  it('should show the server error and not download on non-2xx preflight', async () => {
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => ({
      ok: false,
      text: async () => '{"error":"video clip failed"}',
    }))
    vi.stubGlobal('fetch', fetchMock)

    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata()
    setMediaState({ currentTime: 2 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为起点' }))
    setMediaState({ currentTime: 6 })
    fireEvent.timeUpdate(getVideo())
    fireEvent.click(screen.getByRole('button', { name: '设为终点' }))

    fireEvent.click(screen.getByRole('button', { name: /下载片段/ }))

    expect(await screen.findByText('片段生成失败，请重试')).toBeInTheDocument()
    expect(clickSpy).not.toHaveBeenCalled()
    vi.unstubAllGlobals()
  })

  it('should explain AV1 and missing MSE instead of playing', async () => {
    const { unmount } = render(
      <RecordingPreviewPlayer {...defaultProps} fileName="[AV1][101][主播A] 2026-10-21-歌回.flv" />,
    )
    expect(await screen.findByText('AV1 编码暂不支持在线预览')).toBeInTheDocument()
    expect(mockPlay).not.toHaveBeenCalled()
    unmount()

    mockIsPlaybackSupported.mockReturnValue(false)
    render(<RecordingPreviewPlayer {...defaultProps} />)
    expect(await screen.findByText('当前浏览器不支持在线预览')).toBeInTheDocument()
  })

  it('should show a playback error when the video element fails', async () => {
    render(<RecordingPreviewPlayer {...defaultProps} />)
    await loadMetadata()

    fireEvent.error(getVideo())

    expect(await screen.findByText('预览暂不可用')).toBeInTheDocument()
  })
})
