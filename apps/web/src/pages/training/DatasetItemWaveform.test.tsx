import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { DatasetItemWaveform } from './DatasetItemWaveform'
import { DatasetItemList } from './DatasetItemList'
import type { WaveformLoader, WaveformLoadOptions } from './waveformLoader'
import type { components } from '../../api/schema-training'
import * as client from '../../api/client'

type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingDatasetItems: vi.fn(),
    updateTrainingDatasetItem: vi.fn(),
    batchDeleteTrainingDatasetItems: vi.fn(),
  }
})

const mockListItems = vi.mocked(client.listTrainingDatasetItems)

const item: TrainingDatasetItem = {
  id: 'i1',
  datasetId: 'd1',
  audioType: 'transcript',
  source: 't1',
  audioUri: 'b/live/101/recordings/a.flv',
  audioKey: 'transcript:t1:1000',
  startMs: 1000,
  endMs: 2500,
  text: '第一句',
  extensions: null,
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
}

function createLoader(mode: 'ready' | 'error' = 'ready') {
  const play = vi.fn()
  const stop = vi.fn()
  const destroy = vi.fn()
  const load = vi.fn((options: WaveformLoadOptions) => {
    if (mode === 'ready') {
      options.onReady?.()
    } else {
      options.onError?.()
    }
    return { play, stop, destroy }
  })
  const loader: WaveformLoader = { load }
  return { loader, load, play, stop, destroy }
}

describe('DatasetItemWaveform', () => {
  it('should render the waveform container and load the clip range url', async () => {
    const { loader, load } = createLoader()

    render(<DatasetItemWaveform item={item} loader={loader} />)

    const container = await screen.findByTestId('waveform-container')
    expect(load).toHaveBeenCalledTimes(1)
    const options = load.mock.calls[0][0]
    expect(options.container).toBe(container)
    expect(options.url).toBe(
      '/api/b/live/101/recordings/a.flv/audio?startMs=1000&endMs=2500',
    )
    expect(options.startMs).toBe(1000)
    expect(options.endMs).toBe(2500)
    expect(await screen.findByRole('button', { name: '播放' })).toBeInTheDocument()
  })

  it('should fall back to a 5 minute preview window when endMs is empty', async () => {
    const { loader, load } = createLoader()

    render(<DatasetItemWaveform item={{ ...item, endMs: null }} loader={loader} />)

    await screen.findByTestId('waveform-container')
    const options = load.mock.calls[0][0]
    expect(options.endMs).toBe(1000 + 300000)
    expect(options.url).toContain('endMs=301000')
    expect(screen.getByText('试听预览上限 5 分钟')).toBeInTheDocument()
  })

  it('should play the clip range and stop on demand', async () => {
    const { loader, play, stop } = createLoader()

    render(<DatasetItemWaveform item={item} loader={loader} />)

    fireEvent.click(await screen.findByRole('button', { name: '播放' }))
    expect(play).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: '停止' }))
    expect(stop).toHaveBeenCalledTimes(1)
  })

  it('should show 音频不可用 when the waveform fails to load, keeping the container', async () => {
    const { loader } = createLoader('error')

    render(<DatasetItemWaveform item={item} loader={loader} />)

    expect(await screen.findByText('音频不可用')).toBeInTheDocument()
    expect(screen.getByTestId('waveform-container')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '播放' })).not.toBeInTheDocument()
  })

  it('should degrade to 音频不可用 without loading when audioUri is empty', async () => {
    const { loader, load } = createLoader()

    render(<DatasetItemWaveform item={{ ...item, audioUri: '' }} loader={loader} />)

    expect(await screen.findByText('音频不可用')).toBeInTheDocument()
    expect(load).not.toHaveBeenCalled()
  })

  it('should degrade to 音频不可用 without loading when audioUri cannot be parsed', async () => {
    const { loader, load } = createLoader()

    render(
      <DatasetItemWaveform
        item={{ ...item, audioUri: 'https://example.com/a.flv' }}
        loader={loader}
      />,
    )

    expect(await screen.findByText('音频不可用')).toBeInTheDocument()
    expect(load).not.toHaveBeenCalled()
  })

  it('should destroy the waveform player on unmount', async () => {
    const { loader, destroy } = createLoader()

    const { unmount } = render(<DatasetItemWaveform item={item} loader={loader} />)
    await screen.findByTestId('waveform-container')

    unmount()

    expect(destroy).toHaveBeenCalledTimes(1)
  })

  it('should render the waveform with the clip range url after an item row is clicked', async () => {
    const { loader, load } = createLoader()
    mockListItems.mockResolvedValue([item])

    render(<DatasetItemList datasetId="d1" waveformLoader={loader} />)

    fireEvent.click(await screen.findByText('transcript:t1:1000'))

    expect(await screen.findByTestId('waveform-container')).toBeInTheDocument()
    await waitFor(() => {
      expect(load).toHaveBeenCalledWith(
        expect.objectContaining({
          url: '/api/b/live/101/recordings/a.flv/audio?startMs=1000&endMs=2500',
        }),
      )
    })
  })
})
