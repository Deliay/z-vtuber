import { describe, it, expect, vi } from 'vitest'
import { screen, fireEvent, waitFor } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
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
    expect(await screen.findByRole('button', { name: /播放/ })).toBeInTheDocument()
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

  it('should auto-play as soon as the waveform is ready, and allow stop/replay', async () => {
    const { loader, play, stop } = createLoader()

    render(<DatasetItemWaveform item={item} loader={loader} />)

    // 就绪即自动播放（无需再点「播放」）
    await waitFor(() => expect(play).toHaveBeenCalledTimes(1))

    fireEvent.click(await screen.findByRole('button', { name: /停止/ }))
    expect(stop).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: /播放/ }))
    expect(play).toHaveBeenCalledTimes(2)
  })

  it('should auto-play the 5 minute fallback clip when endMs is empty', async () => {
    const { loader, load, play } = createLoader()

    render(<DatasetItemWaveform item={{ ...item, endMs: null }} loader={loader} />)

    // 兜底区间仍传给 loader（0 基相对时间轴由 loader 计算），并就绪自动播放
    await screen.findByTestId('waveform-container')
    expect(load.mock.calls[0][0].endMs).toBe(1000 + 300000)
    await waitFor(() => expect(play).toHaveBeenCalledTimes(1))
  })

  it('should not cross-play when switching to another item, and destroy the previous player', async () => {
    const first = { play: vi.fn(), stop: vi.fn(), destroy: vi.fn() }
    const second = { play: vi.fn(), stop: vi.fn(), destroy: vi.fn() }
    const load = vi
      .fn()
      .mockImplementationOnce((options: WaveformLoadOptions) => {
        options.onReady?.()
        return first
      })
      .mockImplementationOnce((options: WaveformLoadOptions) => {
        options.onReady?.()
        return second
      })
    const loader: WaveformLoader = { load }

    const { rerender } = render(<DatasetItemWaveform item={item} loader={loader} />)
    await waitFor(() => expect(first.play).toHaveBeenCalledTimes(1))

    rerender(
      <DatasetItemWaveform
        item={{ ...item, id: 'i2', audioKey: 'transcript:t1:2000', startMs: 2000, endMs: 3500 }}
        loader={loader}
      />,
    )

    // 旧实例被销毁且不再播放；新实例各自自动播放一次
    await waitFor(() => expect(second.play).toHaveBeenCalledTimes(1))
    expect(first.destroy).toHaveBeenCalledTimes(1)
    expect(first.play).toHaveBeenCalledTimes(1)
  })

  it('should not replay after unmount', async () => {
    const { loader, play } = createLoader()

    const { unmount } = render(<DatasetItemWaveform item={item} loader={loader} />)
    await waitFor(() => expect(play).toHaveBeenCalledTimes(1))

    unmount()

    expect(play).toHaveBeenCalledTimes(1)
  })

  it('should keep the controls usable and retryable when the loader play() rejects', async () => {
    // 真实 loader 契约：play() 返回 Promise，播放失败以 reject 暴露（onError 仅承载加载失败）
    const play = vi.fn(() => Promise.reject(new Error('autoplay blocked')))
    const stop = vi.fn()
    const destroy = vi.fn()
    const load = vi.fn((options: WaveformLoadOptions) => {
      options.onReady?.()
      return { play, stop, destroy }
    })
    const loader: WaveformLoader = { load }

    render(<DatasetItemWaveform item={item} loader={loader} />)

    // 就绪后自动播放失败 → 非阻断提示，绝不显示「音频不可用」（那是加载失败语义）
    expect(await screen.findByText('音频暂不可用，请点击播放重试')).toBeInTheDocument()
    expect(screen.queryByText('音频不可用')).not.toBeInTheDocument()
    expect(play).toHaveBeenCalledTimes(1)

    // 保留控件，点「播放」可重试
    expect(screen.getByRole('button', { name: /停止/ })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /播放/ }))

    await waitFor(() => expect(play).toHaveBeenCalledTimes(2))
    expect(screen.getByRole('button', { name: /播放/ })).toBeInTheDocument()
    expect(
      await screen.findByText('音频暂不可用，请点击播放重试'),
    ).toBeInTheDocument()
  })

  it('should show 音频不可用 when the waveform fails to load, keeping the container', async () => {
    const { loader } = createLoader('error')

    render(<DatasetItemWaveform item={item} loader={loader} />)

    expect(await screen.findByText('音频不可用')).toBeInTheDocument()
    expect(screen.getByTestId('waveform-container')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /播放/ })).not.toBeInTheDocument()
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

    await screen.findByText('第一句')
    fireEvent.click(screen.getByLabelText('音频信息'))
    fireEvent.click(screen.getByText('transcript:t1:1000'))

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
