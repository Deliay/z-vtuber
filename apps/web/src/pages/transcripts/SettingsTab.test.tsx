import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor, fireEvent } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { SettingsTab } from './SettingsTab'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockGetSettings = vi.mocked(client.getTranscriptSettings)
const mockSetRoom = vi.mocked(client.setRoomAutoEnqueue)
const mockListModels = vi.mocked(client.listTranscriptModels)
const mockCreateModel = vi.mocked(client.createTranscriptModel)
const mockUpdateModel = vi.mocked(client.updateTranscriptModel)
const mockGetCandidates = vi.mocked(client.getTranscriptCandidateRooms)
const mockDeleteSettings = vi.mocked(client.deleteTranscriptSettings)

/** antd Select：点开并选中。 */
async function pickSelect(ariaLabel: string, optionText: string) {
  const combo = screen.getByRole('combobox', { name: ariaLabel }) as HTMLElement
  fireEvent.mouseDown(combo)
  fireEvent.click(await screen.findByTitle(optionText))
}

describe('SettingsTab', () => {
  beforeEach(() => {
    vi.resetAllMocks()

    mockGetSettings.mockResolvedValue([
      { roomId: 101, mid: 1, anchorName: '主播A', enabled: true, modelId: 'm1', modelName: '模型A' },
    ])
    mockListModels.mockResolvedValue([
      {
        id: 'm1',
        name: '模型A',
        provider: 'ali-fun-asr',
        type: 'streaming',
        model: 'fun-asr-realtime',
        enabled: true,
        maxConcurrency: 1,
      },
      {
        id: 'm2',
        name: '模型B',
        provider: 'ali-fun-asr',
        type: 'streaming',
        model: 'paraformer-realtime-v2',
        enabled: false,
        maxConcurrency: 2,
      },
    ])
    mockCreateModel.mockResolvedValue({ id: 'm3', name: '新模型' })
    mockUpdateModel.mockResolvedValue({ id: 'm2' })
    mockSetRoom.mockResolvedValue({ roomId: 101, enabled: false })
    mockGetCandidates.mockResolvedValue([
      { roomId: 101, mid: 1, anchorName: '主播A' },
      { roomId: 202, mid: 2, anchorName: null },
    ])
    mockDeleteSettings.mockResolvedValue(undefined)
  })

  it('should toggle auto recognition per anchor', async () => {
    render(<SettingsTab />)

    fireEvent.click(await screen.findByText('关闭'))

    await waitFor(() => {
      expect(mockSetRoom).toHaveBeenCalledWith(101, false)
    })
  })

  it('should add auto recognition with a selected model and refresh the settings list', async () => {
    render(<SettingsTab />)

    await screen.findByRole('button', { name: '添加自动识别' })

    await pickSelect('直播间号', '202')
    await pickSelect('添加识别模型', '模型A')
    fireEvent.click(screen.getByRole('button', { name: '添加自动识别' }))

    await waitFor(() => {
      expect(mockSetRoom).toHaveBeenCalledWith(202, true, 'm1')
    })
    await waitFor(() => {
      expect(mockGetSettings).toHaveBeenCalledTimes(2)
    })
  })

  it('should block adding auto recognition without a model and not call the api', async () => {
    render(<SettingsTab />)

    await screen.findByRole('button', { name: '添加自动识别' })

    await pickSelect('直播间号', '202')
    fireEvent.click(screen.getByRole('button', { name: '添加自动识别' }))

    expect(await screen.findByText('请先选择识别模型')).toBeInTheDocument()
    expect(mockSetRoom).not.toHaveBeenCalled()
  })

  it('should render the add-automation room as a candidate select with anchor labels', async () => {
    render(<SettingsTab />)

    const combo = await screen.findByRole('combobox', { name: '直播间号' })
    expect(combo).toBeInTheDocument()
    fireEvent.mouseDown(combo)
    expect(await screen.findByTitle('主播A（101）')).toBeInTheDocument()
    expect(screen.getByTitle('202')).toBeInTheDocument()
  })

  it('should disable submit and show a hint when there are no candidate rooms', async () => {
    mockGetCandidates.mockResolvedValue([])

    render(<SettingsTab />)

    await screen.findByText('暂无可选主播')
    expect(screen.getByRole('button', { name: '添加自动识别' })).toBeDisabled()
    expect(mockSetRoom).not.toHaveBeenCalled()
  })

  it('should confirm and delete a settings row then refresh', async () => {
    render(<SettingsTab />)

    await screen.findByText('关闭')
    fireEvent.click(screen.getByRole('button', { name: '删除自动识别-101' }))

    fireEvent.click(await screen.findByRole('button', { name: '确认删除' }))

    await waitFor(() => {
      expect(mockDeleteSettings).toHaveBeenCalledWith(101)
    })
    await waitFor(() => {
      expect(mockGetSettings).toHaveBeenCalledTimes(2)
    })
  })

  it('should not delete when the confirmation is dismissed', async () => {
    render(<SettingsTab />)

    await screen.findByText('关闭')
    fireEvent.click(screen.getByRole('button', { name: '删除自动识别-101' }))
    fireEvent.click(await screen.findByRole('button', { name: '返回' }))

    expect(mockDeleteSettings).not.toHaveBeenCalled()
  })

  it('should flag unbound settings as pending and block enabling without a model', async () => {
    mockGetSettings.mockResolvedValue([
      { roomId: 103, mid: 3, anchorName: '主播C', enabled: false, modelId: null, modelName: null },
    ])

    render(<SettingsTab />)

    expect(await screen.findByText('待完善')).toBeInTheDocument()

    fireEvent.click(screen.getByText('开启'))

    expect(await screen.findByText('请先选择识别模型，自动转写需要绑定模型')).toBeInTheDocument()
    expect(mockSetRoom).not.toHaveBeenCalled()
  })

  it('should bind a model when enabling a pending setting', async () => {
    mockGetSettings.mockResolvedValue([
      { roomId: 104, mid: 4, anchorName: '主播D', enabled: false, modelId: null, modelName: null },
    ])

    render(<SettingsTab />)

    await screen.findByText('待完善')
    await pickSelect('识别模型-104', '模型A')
    fireEvent.click(screen.getByText('开启'))

    await waitFor(() => {
      expect(mockSetRoom).toHaveBeenCalledWith(104, true, 'm1')
    })
  })

  it('should add a model with the recognition model and without echoing the api key', async () => {
    render(<SettingsTab />)

    await screen.findByText('添加模型')

    fireEvent.change(screen.getByLabelText('模型名称'), { target: { value: '新模型' } })
    fireEvent.change(screen.getByLabelText('识别模型'), { target: { value: 'fun-asr-realtime' } })
    fireEvent.change(screen.getByLabelText('API Key'), { target: { value: 'super-secret' } })
    fireEvent.click(screen.getByRole('button', { name: '添加' }))

    await waitFor(() => {
      expect(mockCreateModel).toHaveBeenCalledTimes(1)
    })

    const payload = mockCreateModel.mock.calls[0][0]
    expect(payload.apiKey).toBe('super-secret')
    expect(payload.provider).toBe('ali-fun-asr')
    expect(payload.model).toBe('fun-asr-realtime')

    await waitFor(() => {
      expect((screen.getByLabelText('API Key') as HTMLInputElement).value).toBe('')
    })
    expect(screen.queryByText('super-secret')).not.toBeInTheDocument()
  })

  it('should require a recognition model when creating a model', async () => {
    render(<SettingsTab />)

    await screen.findByText('添加模型')

    fireEvent.change(screen.getByLabelText('模型名称'), { target: { value: '新模型' } })
    fireEvent.change(screen.getByLabelText('API Key'), { target: { value: 'secret' } })
    fireEvent.click(screen.getByRole('button', { name: '添加' }))

    expect(await screen.findByText('请输入识别模型')).toBeInTheDocument()
    expect(mockCreateModel).not.toHaveBeenCalled()
  })

  it('should enable/disable and update max concurrency without a default marker', async () => {
    render(<SettingsTab />)

    await screen.findByRole('button', { name: '禁用' })

    expect(screen.queryByRole('button', { name: '设为默认' })).not.toBeInTheDocument()
    expect(screen.queryByText('未配置默认模型，自动识别需要先设置默认模型')).not.toBeInTheDocument()

    fireEvent.click(screen.getAllByRole('button', { name: '禁用' })[0])
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m1', { enabled: false })
    })

    fireEvent.click(screen.getByRole('button', { name: '启用' }))
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m2', { enabled: true })
    })

    fireEvent.change(screen.getByLabelText('最大并发-模型B'), { target: { value: '3' } })
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
  })

  it('should clamp a negative max concurrency to 1 on create and update', async () => {
    render(<SettingsTab />)

    await screen.findByText('添加模型')

    fireEvent.change(screen.getByLabelText('最大并发-模型B'), { target: { value: '-3' } })
    fireEvent.keyDown(screen.getByLabelText('最大并发-模型B'), { key: 'Enter' })
    fireEvent.click(screen.getAllByRole('button', { name: '保存' })[1])
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m2', { maxConcurrency: 1 })
    })

    fireEvent.change(screen.getByLabelText('模型名称'), { target: { value: '负并发模型' } })
    fireEvent.change(screen.getByLabelText('识别模型'), { target: { value: 'fun-asr-realtime' } })
    fireEvent.change(screen.getByLabelText('API Key'), { target: { value: 'secret' } })
    fireEvent.change(screen.getByLabelText('最大并发数'), { target: { value: '-5' } })
    fireEvent.keyDown(screen.getByLabelText('最大并发数'), { key: 'Enter' })
    fireEvent.click(screen.getByRole('button', { name: '添加' }))

    await waitFor(() => {
      expect(mockCreateModel).toHaveBeenCalledTimes(1)
    })
    expect(mockCreateModel.mock.calls[0][0].maxConcurrency).toBe(1)
  })
})
