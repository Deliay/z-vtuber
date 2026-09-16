import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, fireEvent } from '@testing-library/react'
import { SettingsTab } from './SettingsTab'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockGetSettings = vi.mocked(client.getTranscriptSettings)
const mockSetRoom = vi.mocked(client.setRoomAutoEnqueue)
const mockListModels = vi.mocked(client.listTranscriptModels)
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

  it('should add auto recognition for a valid room and refresh the settings list', async () => {
    render(<SettingsTab />)

    await screen.findByRole('button', { name: '添加自动识别' })

    fireEvent.change(screen.getByLabelText('直播间号'), { target: { value: '202' } })
    fireEvent.click(screen.getByRole('button', { name: '添加自动识别' }))

    await waitFor(() => {
      expect(mockSetRoom).toHaveBeenCalledWith(202, true)
    })
    await waitFor(() => {
      expect(mockGetSettings).toHaveBeenCalledTimes(2)
    })
  })

  it('should show a validation error for an invalid room id and not call the api', async () => {
    render(<SettingsTab />)

    await screen.findByRole('button', { name: '添加自动识别' })

    fireEvent.change(screen.getByLabelText('直播间号'), { target: { value: '0' } })
    fireEvent.click(screen.getByRole('button', { name: '添加自动识别' }))

    expect(await screen.findByText('请输入有效的直播间号')).toBeInTheDocument()
    expect(mockSetRoom).not.toHaveBeenCalled()
  })

  it('should hint that a default model is required for auto recognition', async () => {
    mockListModels.mockResolvedValue([
      {
        id: 'm1',
        name: '非默认模型',
        provider: 'ali-fun-asr',
        type: 'streaming',
        enabled: true,
        isDefault: false,
        maxConcurrency: 1,
      },
    ])

    render(<SettingsTab />)

    expect(
      await screen.findByText('未配置默认模型，自动识别需要先设置默认模型'),
    ).toBeInTheDocument()
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
    expect(screen.getByRole('button', { name: '重试' })).toBeInTheDocument()
  })

  it('should keep settings usable and offer retry after an operation failure', async () => {
    mockSetRoom.mockRejectedValueOnce(new Error('network error'))

    render(<SettingsTab />)

    fireEvent.click(await screen.findByText('关闭'))

    expect(await screen.findByText('自动识别设置更新失败')).toBeInTheDocument()
    expect(screen.getByText('识别设置')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '添加自动识别' })).toBeInTheDocument()

    mockSetRoom.mockResolvedValueOnce({ roomId: 101, enabled: false })
    fireEvent.click(screen.getByRole('button', { name: '重试' }))

    await waitFor(() => {
      expect(mockSetRoom).toHaveBeenCalledTimes(2)
    })
    await waitFor(() => {
      expect(screen.queryByText('自动识别设置更新失败')).not.toBeInTheDocument()
    })
  })

  it('should clamp a negative max concurrency to 1 on create and update', async () => {
    render(<SettingsTab />)

    await screen.findByText('添加模型')

    fireEvent.change(screen.getByLabelText('最大并发-备用模型'), { target: { value: '-3' } })
    fireEvent.click(screen.getAllByRole('button', { name: '保存' })[1])
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m2', { maxConcurrency: 1 })
    })

    fireEvent.change(screen.getByLabelText('模型名称'), { target: { value: '负并发模型' } })
    fireEvent.change(screen.getByLabelText('API Key'), { target: { value: 'secret' } })
    fireEvent.change(screen.getByLabelText('最大并发数'), { target: { value: '-5' } })
    const createForm = screen.getByText('添加').closest('form')
    fireEvent.submit(createForm as HTMLFormElement)

    await waitFor(() => {
      expect(mockCreateModel).toHaveBeenCalledTimes(1)
    })
    expect(mockCreateModel.mock.calls[0][0].maxConcurrency).toBe(1)
  })
})
