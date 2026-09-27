import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, within, act } from '@testing-library/react'
import { SegmentAudioPlayer } from './SegmentAudioPlayer'
import type { components } from '../../api/schema-recording-transcript'

type TranscriptTask = components['schemas']['TranscriptTask']
type TranscriptSegment = components['schemas']['TranscriptSegment']

class MockAudio extends EventTarget {
  static instances: MockAudio[] = []
  src = ''
  paused = true
  currentTime = 0
  preload = ''
  play = vi.fn((): Promise<void> => {
    this.paused = false
    return Promise.resolve()
  })
  pause = vi.fn((): void => {
    this.paused = true
  })
  removeAttribute = vi.fn((name: string): void => {
    if (name === 'src') {
      this.src = ''
    }
  })

  constructor() {
    super()
    MockAudio.instances.push(this)
  }
}

vi.stubGlobal('Audio', MockAudio)

const task: TranscriptTask = {
  id: 't1',
  status: 'DONE',
  roomId: 101,
  title: '晚间歌回',
  file: '[RAW] a.flv',
  offsetMs: 0,
}

const segments: TranscriptSegment[] = [
  { start: 0, end: 1000, text: '第一句' },
  { start: 1000, end: 2500, text: '第二句' },
]

function getAudio(): MockAudio {
  const audio = MockAudio.instances[0]
  if (!audio) {
    throw new Error('audio instance was not created')
  }
  return audio
}

function emitAudioEvent(audio: MockAudio, type: string): void {
  act(() => {
    audio.dispatchEvent(new Event(type))
  })
}

function playRow(row: HTMLElement): void {
  fireEvent.click(within(row).getByRole('button', { name: '播放' }))
}

describe('SegmentAudioPlayer', () => {
  beforeEach(() => {
    MockAudio.instances = []
  })

  it('sets the clip url, plays and shows the current sentence in the unified player', () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')

    playRow(first)

    expect(audio.src).toBe(
      '/api/b/live/101/recordings/%5BRAW%5D%20a.flv/audio?startMs=0&endMs=1000',
    )
    expect(audio.play).toHaveBeenCalledTimes(1)
    expect(screen.getByText('0:00 - 0:01')).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
    expect(within(first).getByRole('button', { name: '停止' })).toBeInTheDocument()
  })

  it('switches source when another segment is clicked', () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first, second] = screen.getAllByRole('listitem')

    playRow(first)
    playRow(second)

    expect(audio.src).toBe(
      '/api/b/live/101/recordings/%5BRAW%5D%20a.flv/audio?startMs=1000&endMs=2500',
    )
    expect(audio.play).toHaveBeenCalledTimes(2)
    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
    expect(within(second).getByRole('button', { name: '停止' })).toBeInTheDocument()
    expect(screen.getByText('0:01 - 0:02')).toBeInTheDocument()
  })

  it('stops playback when the same segment is clicked again', () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')

    playRow(first)
    fireEvent.click(within(first).getByRole('button', { name: '停止' }))

    expect(audio.pause).toHaveBeenCalled()
    expect(audio.src).toBe('')
    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('stops playback from the unified player stop button', () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')

    playRow(first)
    fireEvent.click(screen.getByRole('button', { name: '停止试听' }))

    expect(audio.pause).toHaveBeenCalled()
    expect(audio.src).toBe('')
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
  })

  it('clears the highlight when playback ends', () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')

    playRow(first)
    emitAudioEvent(audio, 'ended')

    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(screen.queryByText('0:00 - 0:01')).not.toBeInTheDocument()
  })

  it('clears the highlight when the audio is paused', () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')

    playRow(first)
    audio.paused = true
    emitAudioEvent(audio, 'pause')

    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('shows 音频暂不可用 on the audio error event', async () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')

    playRow(first)
    emitAudioEvent(audio, 'error')

    expect(await screen.findByText('音频暂不可用')).toBeInTheDocument()
    expect(within(first).getByRole('button', { name: '播放' })).toBeInTheDocument()
  })

  it('shows 音频暂不可用 when play() rejects', async () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    audio.play.mockImplementationOnce(() => Promise.reject(new Error('blocked')))
    const [first] = screen.getAllByRole('listitem')

    playRow(first)

    expect(await screen.findByText('音频暂不可用')).toBeInTheDocument()
  })

  it('updates the progress bar on timeupdate', () => {
    render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')

    playRow(first)
    audio.currentTime = 0.5
    emitAudioEvent(audio, 'timeupdate')

    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50')
  })

  it('disables the play buttons with a hint when the task has no file', () => {
    render(<SegmentAudioPlayer task={{ ...task, file: null }} segments={segments} />)

    const buttons = screen.getAllByRole('button', { name: '播放' })
    expect(buttons).toHaveLength(2)
    for (const button of buttons) {
      expect(button).toBeDisabled()
      expect(button).toHaveAttribute('title', '音频暂不可用')
    }
  })

  it('pauses and clears src when the task changes', () => {
    const { rerender } = render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')
    playRow(first)

    rerender(<SegmentAudioPlayer task={{ ...task, id: 't2' }} segments={segments} />)

    expect(audio.pause).toHaveBeenCalled()
    expect(audio.src).toBe('')
  })

  it('pauses and clears src on unmount', () => {
    const { unmount } = render(<SegmentAudioPlayer task={task} segments={segments} />)
    const audio = getAudio()
    const [first] = screen.getAllByRole('listitem')
    playRow(first)

    unmount()

    expect(audio.pause).toHaveBeenCalled()
    expect(audio.src).toBe('')
  })

  it('renders the scrollable container bound to the forwarded ref', () => {
    const containerRef: { current: HTMLDivElement | null } = { current: null }
    render(<SegmentAudioPlayer task={task} segments={segments} containerRef={containerRef} />)

    const row = screen.getAllByRole('listitem')[0]
    const container = row.closest('ul')?.parentElement as HTMLElement | null
    expect(container).not.toBeNull()
    expect(container).toBe(containerRef.current)
    expect(container?.style.maxHeight).toBe('24rem')
    expect(container?.style.overflowY).toBe('auto')
  })

  it('clamps the clip range to the non-negative timeline for offset-shifted segments', () => {
    render(
      <SegmentAudioPlayer
        task={{ ...task, offsetMs: -2000 }}
        segments={[{ start: -500, end: 1200, text: '前移句' }]}
      />,
    )
    const audio = getAudio()

    playRow(screen.getAllByRole('listitem')[0])

    expect(audio.src).toBe(
      '/api/b/live/101/recordings/%5BRAW%5D%20a.flv/audio?startMs=0&endMs=1200',
    )
  })

  it('disables playback when the whole shifted segment is before the timeline start', () => {
    render(
      <SegmentAudioPlayer
        task={{ ...task, offsetMs: -3000 }}
        segments={[{ start: -2000, end: -800, text: '越界句' }]}
      />,
    )

    const button = screen.getAllByRole('button', { name: '播放' })[0]
    expect(button).toBeDisabled()
    expect(button).toHaveAttribute('title', '音频暂不可用')
  })

  it('disables playback when the shifted segment ends at the timeline start', () => {
    render(
      <SegmentAudioPlayer
        task={{ ...task, offsetMs: -3000 }}
        segments={[{ start: -1000, end: 0, text: '零长句' }]}
      />,
    )

    const button = screen.getAllByRole('button', { name: '播放' })[0]
    expect(button).toBeDisabled()
    expect(button).toHaveAttribute('title', '音频暂不可用')
  })
})
