import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, fireEvent } from '@testing-library/react'
import { SettingsTab } from './SettingsTab'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockGetSettings = vi.mocked(client.getTranscriptSettings)
const mockSetRoom = vi.mocked(client.setRoomAutoEnqueue)
const mockListRecordings = vi.mocked(client.listTranscriptRecordings)
const mockListModels = vi.mocked(client.listTranscriptModels)
const mockGetRoomTranscripts = vi.mocked(client.getRoomTranscripts)
const mockEnqueue = vi.mocked(client.enqueueTranscript)
const mockRetry = vi.mocked(client.retryTranscriptTask)
const mockCreateModel = vi.mocked(client.createTranscriptModel)
const mockUpdateModel = vi.mocked(client.updateTranscriptModel)

describe('SettingsTab', () => {
  beforeEach(() => {
    vi.resetAllMocks()

    mockGetSettings.mockResolvedValue([
      { roomId: 101, mid: 1, anchorName: '主播A', enabled: true },
    ])
    mockListModels.mockResolvedValue([
      {
        id: 'm1',
        name: '默认模型',
        provider: 'ali-fun-asr',
        type: 'streaming',
        enabled: true,
        isDefault: true,
        maxConcurrency: 1,
      },
      {
        id: 'm2',
        name: '备用模型',
        provider: 'ali-fun-asr',
        type: 'streaming',
        enabled: false,
        isDefault: false,
        maxConcurrency: 2,
      },
    ])
    mockListRecordings.mockResolvedValue([
      { id: 'r1', roomId: 101, mid: 1, title: '晚间歌回', files: [], startedAt: '2026-09-16T12:00:00Z' },
    ])
    mockGetRoomTranscripts.mockResolvedValue({
      roomId: 101,
      autoEnqueueEnabled: true,
      items: [
        {
          recordingId: 'r1',
          title: '晚间歌回',
          startedAt: '2026-09-16T12:00:00Z',
          endedAt: null,
          tasks: [
            {
              taskId: 't1',
              modelId: 'm1',
              modelName: '默认模型',
              status: 'FAILED',
              position: 0,
              source: 'manual',
              updatedAt: '2026-09-16T13:00:00Z',
            },
          ],
        },
      ],
    })
    mockEnqueue.mockResolvedValue({ taskId: 't2', status: 'PENDING' })
    mockRetry.mockResolvedValue({ id: 't1', status: 'RUNNING' })
    mockCreateModel.mockResolvedValue({ id: 'm3', name: '新模型' })
    mockUpdateModel.mockResolvedValue({ id: 'm2' })
    mockSetRoom.mockResolvedValue({ roomId: 101, enabled: false })
  })

  it('should toggle auto recognition per anchor', async () => {
    render(<SettingsTab />)

    fireEvent.click(await screen.findByText('关闭'))

    await waitFor(() => {
      expect(mockSetRoom).toHaveBeenCalledWith(101, false)
    })
  })

  it('should enqueue a manual transcript task with recording and model', async () => {
    render(<SettingsTab />)

    await screen.findByText('发起识别')

    fireEvent.change(screen.getByLabelText('录播场次'), { target: { value: 'r1' } })
    fireEvent.change(screen.getByLabelText('识别模型'), { target: { value: 'm1' } })
    fireEvent.click(screen.getByText('发起识别'))

    await waitFor(() => {
      expect(mockEnqueue).toHaveBeenCalledWith('r1', 'm1')
    })
    expect(await screen.findByText(/已创建任务 t2/)).toBeInTheDocument()
  })

  it('should query a room transcripts and retry a FAILED task', async () => {
    render(<SettingsTab />)

    await screen.findByText('查询')

    fireEvent.change(screen.getByLabelText('主播'), { target: { value: '101' } })
    fireEvent.click(screen.getByText('查询'))

    await waitFor(() => {
      expect(mockGetRoomTranscripts).toHaveBeenCalledWith(101)
    })

    const retryButton = await screen.findByText('重试')
    fireEvent.click(retryButton)

    await waitFor(() => {
      expect(mockRetry).toHaveBeenCalledWith('t1')
    })
  })

  it('should add a model without echoing the api key afterwards', async () => {
    render(<SettingsTab />)

    await screen.findByText('添加模型')

    fireEvent.change(screen.getByLabelText('模型名称'), { target: { value: '新模型' } })
    fireEvent.change(screen.getByLabelText('API Key'), { target: { value: 'super-secret' } })
    fireEvent.click(screen.getByText('添加'))

    await waitFor(() => {
      expect(mockCreateModel).toHaveBeenCalledTimes(1)
    })

    const payload = mockCreateModel.mock.calls[0][0]
    expect(payload.apiKey).toBe('super-secret')
    expect(payload.provider).toBe('ali-fun-asr')

    expect((screen.getByLabelText('API Key') as HTMLInputElement).value).toBe('')
    expect(screen.queryByText('super-secret')).not.toBeInTheDocument()
  })

  it('should enable/disable, set default and update max concurrency', async () => {
    render(<SettingsTab />)

    await screen.findByRole('button', { name: '禁用' })

    fireEvent.click(screen.getAllByRole('button', { name: '禁用' })[0])
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m1', { enabled: false })
    })

    fireEvent.click(screen.getByRole('button', { name: '启用' }))
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m2', { enabled: true })
    })

    fireEvent.click(screen.getByRole('button', { name: '设为默认' }))
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m2', { isDefault: true })
    })

    fireEvent.change(screen.getByLabelText('最大并发-备用模型'), { target: { value: '3' } })
    fireEvent.click(screen.getAllByRole('button', { name: '保存' })[1])
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m2', { maxConcurrency: 3 })
    })
  })

  it('should show an error when the service is unavailable', async () => {
    mockGetSettings.mockRejectedValue(new Error('network error'))

    render(<SettingsTab />)

    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })
})
