import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { ModelSettingsTab } from './ModelSettingsTab'
import * as client from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingProvider = components['schemas']['TrainingProvider']
type TrainingModel = components['schemas']['TrainingModel']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingProviders: vi.fn(),
    createTrainingProvider: vi.fn(),
    updateTrainingProvider: vi.fn(),
    deleteTrainingProvider: vi.fn(),
    discoverTrainingModels: vi.fn(),
    importTrainingModels: vi.fn(),
    listTrainingModels: vi.fn(),
    createTrainingModel: vi.fn(),
    updateTrainingModel: vi.fn(),
    deleteTrainingModel: vi.fn(),
    listTrainingFeaturePoints: vi.fn(),
  }
})

const mockListProviders = vi.mocked(client.listTrainingProviders)
const mockCreateProvider = vi.mocked(client.createTrainingProvider)
const mockUpdateProvider = vi.mocked(client.updateTrainingProvider)
const mockDeleteProvider = vi.mocked(client.deleteTrainingProvider)
const mockDiscover = vi.mocked(client.discoverTrainingModels)
const mockImport = vi.mocked(client.importTrainingModels)
const mockListModels = vi.mocked(client.listTrainingModels)
const mockCreateModel = vi.mocked(client.createTrainingModel)
const mockUpdateModel = vi.mocked(client.updateTrainingModel)
const mockDeleteModel = vi.mocked(client.deleteTrainingModel)
const mockListFeaturePoints = vi.mocked(client.listTrainingFeaturePoints)

function apiError(status: number, serverMessage: string): Error {
  return Object.assign(new Error('API request failed'), { status, serverMessage })
}

const providerA: TrainingProvider = {
  id: 'p1',
  name: 'openai-prod',
  type: 'openai',
  endpoint: 'https://api.openai.com/v1',
  apiKeySet: true,
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
}

const providerB: TrainingProvider = {
  ...providerA,
  id: 'p2',
  name: 'claude-prod',
  type: 'claude',
  endpoint: 'https://api.anthropic.com',
  apiKeySet: false,
}

const modelA: TrainingModel = {
  id: 'm1',
  providerId: 'p1',
  providerName: 'openai-prod',
  model: 'gpt-4o',
  slug: 'openai-prod/gpt-4o',
  enabled: true,
  inputCapabilities: ['audio', 'text'],
  outputCapabilities: ['text'],
  createdAt: '2026-09-27T00:00:00Z',
  updatedAt: '2026-09-27T00:00:00Z',
}

const modelB: TrainingModel = {
  ...modelA,
  id: 'm2',
  model: 'gpt-4o-mini',
  slug: 'openai-prod/gpt-4o-mini',
  enabled: false,
}

describe('ModelSettingsTab', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListProviders.mockResolvedValue([])
    mockListModels.mockResolvedValue([])
    mockListFeaturePoints.mockResolvedValue([])
  })

  it('should render the providers table and never show the plaintext api key', async () => {
    mockListProviders.mockResolvedValue([providerA, providerB])
    mockListModels.mockResolvedValue([modelA])

    render(<ModelSettingsTab />)

    expect((await screen.findAllByText('openai-prod')).length).toBeGreaterThan(0)
    const providersTable = screen.getByRole('columnheader', { name: '名称' }).closest('table') as HTMLElement
    for (const header of ['名称', '模型类型', 'Endpoint', 'API key 状态', '操作']) {
      expect(
        within(providersTable).getByRole('columnheader', { name: header }),
      ).toBeInTheDocument()
    }
    expect(screen.getByText('已配置')).toBeInTheDocument()
    expect(screen.getByText('未配置')).toBeInTheDocument()
    expect(screen.queryByText(/sk-/)).not.toBeInTheDocument()
  })

  it('should show the empty state guiding provider and model setup', async () => {
    render(<ModelSettingsTab />)

    expect(
      await screen.findByText('请先添加 Provider 并获取/添加模型'),
    ).toBeInTheDocument()
    expect(
      screen.getByText('暂无模型，请通过「获取模型」导入或手动添加'),
    ).toBeInTheDocument()
  })

  it('should show the models empty guide when a provider exists but has no models', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockListModels.mockResolvedValue([])

    render(<ModelSettingsTab />)

    expect(await screen.findByText('openai-prod')).toBeInTheDocument()
    // Provider 已存在、模型为空（最常见中间态）时，Models 区域仍须给出引导
    expect(
      screen.getByText('暂无模型，请通过「获取模型」导入或手动添加'),
    ).toBeInTheDocument()
    // 已有 Provider，不再显示 Provider 区域引导
    expect(
      screen.queryByText('请先添加 Provider 并获取/添加模型'),
    ).not.toBeInTheDocument()
  })

  it('should only offer OpenAI and Claude as provider types', async () => {
    render(<ModelSettingsTab />)
    await screen.findByText('请先添加 Provider 并获取/添加模型')

    fireEvent.click(screen.getByRole('button', { name: '新增 Provider' }))

    const select = screen.getByLabelText('模型类型')
    const options = within(select).getAllByRole('option')
    expect(options.map((option) => (option as HTMLOptionElement).value)).toEqual([
      'openai',
      'claude',
    ])
    expect(options.map((option) => option.textContent)).toEqual(['OpenAI', 'Claude'])
  })

  it('should create a provider from the four fields', async () => {
    render(<ModelSettingsTab />)
    await screen.findByText('请先添加 Provider 并获取/添加模型')

    mockListProviders.mockResolvedValue([providerA])

    fireEvent.click(screen.getByRole('button', { name: '新增 Provider' }))
    fireEvent.change(screen.getByLabelText('名称'), { target: { value: 'openai-prod' } })
    fireEvent.change(screen.getByLabelText('模型类型'), { target: { value: 'claude' } })
    fireEvent.change(screen.getByLabelText('Endpoint'), {
      target: { value: 'https://api.anthropic.com' },
    })
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'sk-1' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => {
      expect(mockCreateProvider).toHaveBeenCalledWith({
        name: 'openai-prod',
        type: 'claude',
        endpoint: 'https://api.anthropic.com',
        apiKey: 'sk-1',
      })
    })
    expect(await screen.findByText('openai-prod')).toBeInTheDocument()
  })

  it('should edit a provider and leave the api key unchanged when it is empty', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockUpdateProvider.mockResolvedValue(providerA)

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '编辑 openai-prod' }))

    // 编辑态不回显明文 key，且提示留空表示不修改
    expect(screen.getByLabelText('API key')).toHaveValue('')
    expect(
      screen.getByText('API key 留空表示不修改，保存后不会回显'),
    ).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => {
      expect(mockUpdateProvider).toHaveBeenCalledWith('p1', {
        name: 'openai-prod',
        type: 'openai',
        endpoint: 'https://api.openai.com/v1',
      })
    })
    const body = mockUpdateProvider.mock.calls[0][1]
    expect(body).not.toHaveProperty('apiKey')
  })

  it('should delete a provider after a confirm mentioning the child model count', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    mockListProviders.mockResolvedValue([providerA])
    mockListModels.mockResolvedValue([modelA, modelB])
    mockDeleteProvider.mockResolvedValue(undefined)

    render(<ModelSettingsTab />)
    await screen.findAllByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '删除 openai-prod' }))

    expect(confirmSpy).toHaveBeenCalledWith(
      expect.stringContaining('其下 2 个模型将一并删除'),
    )
    await waitFor(() => {
      expect(mockDeleteProvider).toHaveBeenCalledWith('p1')
    })
  })

  it('should not delete a provider when the confirm is rejected', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    mockListProviders.mockResolvedValue([providerA])

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '删除 openai-prod' }))

    expect(mockDeleteProvider).not.toHaveBeenCalled()
  })

  it('should discover models and report the imported and skipped counts', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockDiscover.mockResolvedValue([{ model: 'gpt-4o' }, { model: 'gpt-4o-mini' }])
    mockImport.mockResolvedValue({ imported: 1, skipped: 1 })

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))

    await waitFor(() => {
      expect(mockDiscover).toHaveBeenCalledWith('p1')
    })
    await screen.findByRole('dialog', { name: '获取模型 - openai-prod' })

    fireEvent.click(screen.getByLabelText('导入 gpt-4o'))
    fireEvent.click(screen.getByRole('button', { name: '导入选中' }))

    await waitFor(() => {
      expect(mockImport).toHaveBeenCalledWith('p1', ['gpt-4o'])
    })
    expect(await screen.findByRole('status')).toHaveTextContent('已导入 1 个，跳过 1 个')
  })

  it('should show 模型服务不可用 when discovering fails', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockDiscover.mockRejectedValue(apiError(502, '{"error":"provider service unavailable"}'))

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('模型服务不可用')
  })

  it('should distinguish discover failures with the shared apiErrorMessage', async () => {
    mockListProviders.mockResolvedValue([providerA])

    mockDiscover.mockRejectedValue(apiError(502, '{"error":"invalid provider response"}'))
    const first = render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')
    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('模型服务响应非法')
    first.unmount()

    mockDiscover.mockRejectedValue(apiError(404, '{"error":"provider not found"}'))
    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')
    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Provider 不存在')
  })

  it('should keep the discover dialog open while importing so the result feedback is not lost', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockDiscover.mockResolvedValue([{ model: 'gpt-4o' }])
    let resolveImport: (value: { imported: number; skipped: number }) => void = () => {}
    mockImport.mockReturnValue(
      new Promise((resolve) => {
        resolveImport = resolve
      }),
    )

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')
    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))
    fireEvent.click(await screen.findByLabelText('导入 gpt-4o'))
    fireEvent.click(screen.getByRole('button', { name: '导入选中' }))

    const dialog = screen.getByRole('dialog', { name: '获取模型 - openai-prod' })
    const overlay = dialog.parentElement as HTMLElement
    // 导入中：遮罩点击与关闭按钮都不应关闭弹窗（保住结果反馈）
    fireEvent.click(overlay)
    expect(screen.getByRole('dialog', { name: '获取模型 - openai-prod' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '关闭' })).toBeDisabled()

    resolveImport({ imported: 1, skipped: 1 })
    expect(await screen.findByRole('status')).toHaveTextContent('已导入 1 个，跳过 1 个')
    expect(screen.getByRole('dialog', { name: '获取模型 - openai-prod' })).toBeInTheDocument()
  })

  it('should display the slug, add a model via the provider select, and toggle/delete', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockListModels.mockResolvedValue([modelA, modelB])
    mockCreateModel.mockResolvedValue({
      ...modelA,
      id: 'm3',
      model: 'gpt-4o-audio',
      slug: 'openai-prod/gpt-4o-audio',
    })
    mockUpdateModel.mockResolvedValue(modelA)
    mockDeleteModel.mockResolvedValue(undefined)
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod/gpt-4o')

    // 手动添加：Provider 为下拉选择已有 Providers
    fireEvent.click(screen.getByRole('button', { name: '手动添加模型' }))
    const providerSelect = screen.getByLabelText('所属 Provider')
    expect(providerSelect.tagName).toBe('SELECT')
    expect(
      within(providerSelect).getByRole('option', { name: 'openai-prod' }),
    ).toBeInTheDocument()
    fireEvent.change(providerSelect, { target: { value: 'p1' } })
    fireEvent.change(screen.getByLabelText('模型名'), { target: { value: 'gpt-4o-audio' } })
    fireEvent.click(screen.getByRole('button', { name: '确认添加' }))

    await waitFor(() => {
      expect(mockCreateModel).toHaveBeenCalledWith({
        providerId: 'p1',
        model: 'gpt-4o-audio',
        inputCapabilities: [],
        outputCapabilities: [],
      })
    })

    fireEvent.click(screen.getByRole('button', { name: '停用 openai-prod/gpt-4o' }))
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m1', { enabled: false })
    })

    fireEvent.click(screen.getByRole('button', { name: '启用 openai-prod/gpt-4o-mini' }))
    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m2', { enabled: true })
    })

    fireEvent.click(screen.getByRole('button', { name: '删除 openai-prod/gpt-4o' }))
    expect(confirmSpy).toHaveBeenCalledWith(expect.stringContaining('openai-prod/gpt-4o'))
    await waitFor(() => {
      expect(mockDeleteModel).toHaveBeenCalledWith('m1')
    })
  })

  it('should ignore repeated manual model submissions while in flight', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockListModels.mockResolvedValue([])
    let resolveCreate: (value: TrainingModel) => void = () => {}
    mockCreateModel.mockReturnValue(
      new Promise((resolve) => {
        resolveCreate = resolve
      }),
    )

    render(<ModelSettingsTab />)
    await screen.findByText('暂无模型，请通过「获取模型」导入或手动添加')

    fireEvent.click(screen.getByRole('button', { name: '手动添加模型' }))
    fireEvent.change(screen.getByLabelText('所属 Provider'), { target: { value: 'p1' } })
    fireEvent.change(screen.getByLabelText('模型名'), { target: { value: 'gpt-4o-audio' } })
    const confirm = screen.getByRole('button', { name: '确认添加' })
    fireEvent.click(confirm)
    fireEvent.click(confirm)

    expect(mockCreateModel).toHaveBeenCalledTimes(1)
    await waitFor(() => {
      expect(screen.getByRole('button', { name: '添加中...' })).toBeDisabled()
    })

    resolveCreate({ ...modelA, id: 'm3' })
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: '手动添加模型' })).not.toBeInTheDocument()
    })
  })

  it('should render the input/output capability columns with Chinese labels and the 未设置 empty state', async () => {
    const modelWithoutCapabilities: TrainingModel = {
      ...modelA,
      id: 'm3',
      slug: 'openai-prod/text-embedding',
      inputCapabilities: [],
      outputCapabilities: [],
    }
    mockListProviders.mockResolvedValue([providerA])
    mockListModels.mockResolvedValue([modelA, modelWithoutCapabilities])

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod/gpt-4o')

    const modelsTable = screen
      .getByRole('columnheader', { name: 'slug' })
      .closest('table') as HTMLElement
    expect(within(modelsTable).getByRole('columnheader', { name: '输入能力' })).toBeInTheDocument()
    expect(within(modelsTable).getByRole('columnheader', { name: '输出能力' })).toBeInTheDocument()

    const row = within(modelsTable).getByText('openai-prod/gpt-4o').closest('tr') as HTMLElement
    // 输入 audio,text → 语音、文本；输出 text → 文本
    expect(within(row).getAllByText('语音、文本')).toHaveLength(1)
    expect(within(row).getAllByText('文本')).toHaveLength(1)

    const emptyRow = within(modelsTable)
      .getByText('openai-prod/text-embedding')
      .closest('tr') as HTMLElement
    // 空能力两列都显示「未设置」
    expect(within(emptyRow).getAllByText('未设置')).toHaveLength(2)
  })

  it('should create a model with the selected input and output capabilities', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockCreateModel.mockResolvedValue({ ...modelA, id: 'm3' })

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '手动添加模型' }))
    fireEvent.change(screen.getByLabelText('所属 Provider'), { target: { value: 'p1' } })
    fireEvent.change(screen.getByLabelText('模型名'), { target: { value: 'gpt-4o-audio' } })
    fireEvent.click(screen.getByLabelText('输入能力 语音'))
    fireEvent.click(screen.getByLabelText('输入能力 文本'))
    fireEvent.click(screen.getByLabelText('输出能力 文本'))
    fireEvent.click(screen.getByRole('button', { name: '确认添加' }))

    await waitFor(() => {
      expect(mockCreateModel).toHaveBeenCalledWith({
        providerId: 'p1',
        model: 'gpt-4o-audio',
        inputCapabilities: ['audio', 'text'],
        outputCapabilities: ['text'],
      })
    })
  })

  it('should prefill and save edited capabilities, including clearing them', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockListModels.mockResolvedValue([modelA])
    mockUpdateModel.mockResolvedValue(modelA)

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod/gpt-4o')

    fireEvent.click(screen.getByRole('button', { name: '编辑能力 openai-prod/gpt-4o' }))
    const dialog = await screen.findByRole('dialog', { name: '编辑能力 openai-prod/gpt-4o' })
    // 预填当前能力
    expect(within(dialog).getByLabelText('输入能力 语音')).toBeChecked()
    expect(within(dialog).getByLabelText('输入能力 文本')).toBeChecked()
    expect(within(dialog).getByLabelText('输出能力 文本')).toBeChecked()

    // 清空全部能力（空数组 + 显式提交）
    fireEvent.click(within(dialog).getByLabelText('输入能力 语音'))
    fireEvent.click(within(dialog).getByLabelText('输入能力 文本'))
    fireEvent.click(within(dialog).getByLabelText('输出能力 文本'))
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))

    await waitFor(() => {
      expect(mockUpdateModel).toHaveBeenCalledWith('m1', {
        inputCapabilities: [],
        outputCapabilities: [],
      })
    })
  })
})
