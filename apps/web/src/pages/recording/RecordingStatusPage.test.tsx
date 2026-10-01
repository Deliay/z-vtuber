import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor, fireEvent } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { RecordingStatusPage, RECORDING_REFRESH_INTERVAL } from './RecordingStatusPage'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockListRecordingStatus = vi.mocked(client.listRecordingStatus)
const mockListRecordings = vi.mocked(client.listRecordings)
const mockAudioUrl = vi.mocked(client.recordingAudioUrl)
const mockDownloadUrl = vi.mocked(client.recordingDownloadUrl)

describe('RecordingStatusPage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListRecordingStatus.mockResolvedValue([])
    mockListRecordings.mockResolvedValue([])
    mockAudioUrl.mockReturnValue('/api/b/live/1/live-stream/audio')
    mockDownloadUrl.mockReturnValue('/api/b/live/1/recordings/a.flv')
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('should render title and empty state', async () => {
    render(<RecordingStatusPage />)

    expect(screen.getByText('录制状态')).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByText('暂无录播订阅')).toBeInTheDocument()
    })
  })

  it('should render three states with hostname, session title and files', async () => {
    mockListRecordingStatus.mockResolvedValue([
      { mid: 1, roomId: 101, state: 'not_live', streamer: { name: '主播A' } },
      {
        mid: 2,
        roomId: 202,
        state: 'recording',
        hostName: 'z-vtuber-recording-7d9f',
        sessionTitle: '晚间歌回',
        files: ['[RAW][202][主播B] 2026-09-13-晚间歌回.flv'],
        streamer: { name: '主播B' },
      },
      { mid: 3, roomId: 303, state: 'error', error: 'ffmpeg exited with code 1', streamer: { name: '主播C' } },
    ])

    render(<RecordingStatusPage />)

    await waitFor(() => {
      expect(screen.getByText('未开播')).toBeInTheDocument()
      expect(screen.getByText('录制中')).toBeInTheDocument()
      expect(screen.getByText('录制出错')).toBeInTheDocument()
      expect(screen.getByText('z-vtuber-recording-7d9f')).toBeInTheDocument()
      expect(screen.getByText('晚间歌回')).toBeInTheDocument()
      expect(screen.getByText('[RAW][202][主播B] 2026-09-13-晚间歌回.flv')).toBeInTheDocument()
      expect(screen.getByText('实时音频')).toBeInTheDocument()
    })
  })

  it('should expose the live audio hint to keyboard users via a focus tooltip', async () => {
    mockListRecordingStatus.mockResolvedValue([
      {
        mid: 2,
        roomId: 202,
        state: 'recording',
        sessionTitle: '晚间歌回',
        streamer: { name: '主播B' },
      },
    ])
    mockListRecordings.mockResolvedValue([])

    render(<RecordingStatusPage />)

    const label = await screen.findByText('实时音频')
    const link = label.closest('a') as HTMLElement
    expect(link).not.toBeNull()
    // 悬浮提示对键盘用户可达：focus 链接即出 Tooltip
    fireEvent.focus(link)
    expect(
      (await screen.findAllByRole('tooltip')).some(
        (tip) => tip.textContent === 'FLV 音频流，需支持 FLV 的播放器打开',
      ),
    ).toBe(true)
  })

  it('should load and render historical recordings', async () => {
    mockListRecordingStatus.mockResolvedValue([
      { mid: 1, roomId: 101, state: 'not_live', streamer: { name: '主播A' } },
    ])
    mockListRecordings.mockResolvedValue([
      {
        name: '[RAW][101][主播A] 2026-09-01-歌回.flv',
        size: 1288490189,
        createdAt: '2026-09-01T12:00:00Z',
        info: {
          recordingId: 'rec-101',
          title: '往期歌回',
          mid: 1,
          startedAt: '2026-09-01T12:00:00Z',
          endedAt: '2026-09-01T14:00:00Z',
        },
      },
    ])

    render(<RecordingStatusPage />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /历史录播/ })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: /历史录播/ }))

    await waitFor(() => {
      expect(screen.getByText('[RAW][101][主播A] 2026-09-01-歌回.flv')).toBeInTheDocument()
    })
    expect(screen.getByText('1.2 GB')).toBeInTheDocument()
    expect(screen.getByText('往期歌回')).toBeInTheDocument()
    expect(mockListRecordings).toHaveBeenCalledWith(101)
    expect(mockDownloadUrl).toHaveBeenCalledWith(101, '[RAW][101][主播A] 2026-09-01-歌回.flv')
  })

  it('should render unknown state for missing state', async () => {
    mockListRecordingStatus.mockResolvedValue([{ mid: 9, roomId: 909 }])

    render(<RecordingStatusPage />)

    await waitFor(() => {
      expect(screen.getByText('未知')).toBeInTheDocument()
    })
  })

  it('should show error state when API fails', async () => {
    mockListRecordingStatus.mockRejectedValue(new Error('network error'))

    render(<RecordingStatusPage />)

    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: '重试' })).toBeInTheDocument()
  })

  it('should auto refresh on interval', async () => {
    vi.useFakeTimers()
    mockListRecordingStatus.mockResolvedValue([])

    render(<RecordingStatusPage />)

    await vi.advanceTimersByTimeAsync(RECORDING_REFRESH_INTERVAL)

    expect(mockListRecordingStatus.mock.calls.length).toBeGreaterThanOrEqual(2)
  })
})
