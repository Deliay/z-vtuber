import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor, within } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
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

/** 打开 antd Select 并按选项文本（title）选择。 */
async function pickOption(ariaLabel: string, optionText: string) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: ariaLabel }))
  await waitFor(() => {
    expect(document.querySelectorAll('.ant-select-item-option').length).toBeGreaterThan(0)
  })
  const option = Array.from(document.querySelectorAll('.ant-select-item-option')).find((el) =>
    (el.getAttribute('title') ?? '').includes(optionText),
  )
  if (!option) throw new Error(`option not found: ${optionText}`)
  fireEvent.click(option)
}

describe('ModelSettingsTab', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListProviders.mockResolvedValue([])
    mockListModels.mockResolvedValue([])
  })

  it('should render the providers table and never show the plaintext api key', async () => {
    mockListProviders.mockResolvedValue([providerA, providerB])
    mockListModels.mockResolvedValue([modelA])

    render(<ModelSettingsTab />)

    expect((await screen.findAllByText('openai-prod')).length).toBeGreaterThan(0)
    const providersTable = screen
      .getByRole('columnheader', { name: '名称' })
      .closest('table') as HTMLElement
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

    fireEvent.mouseDown(screen.getByRole('combobox', { name: '模型类型' }))
    await waitFor(() => {
      expect(document.querySelectorAll('.ant-select-item-option').length).toBe(2)
    })
    const labels = Array.from(document.querySelectorAll('.ant-select-item-option')).map(
      (el) => el.textContent,
    )
    expect(labels).toEqual(['OpenAI', 'Claude'])
  })

  it('should create a provider from the four fields', async () => {
    render(<ModelSettingsTab />)
    await screen.findByText('请先添加 Provider 并获取/添加模型')

    mockListProviders.mockResolvedValue([providerA])

    fireEvent.click(screen.getByRole('button', { name: '新增 Provider' }))
    fireEvent.change(screen.getByLabelText('名称'), { target: { value: 'openai-prod' } })
    await pickOption('模型类型', 'Claude')
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
    mockListProviders.mockResolvedValue([providerA])
    mockListModels.mockResolvedValue([modelA, modelB])
    mockDeleteProvider.mockResolvedValue(undefined)

    render(<ModelSettingsTab />)
    await screen.findAllByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '删除 openai-prod' }))

    expect(await screen.findByText(/其下 2 个模型将一并删除/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '确认删除' }))

    await waitFor(() => {
      expect(mockDeleteProvider).toHaveBeenCalledWith('p1')
    })
  })

  it('should not delete a provider when the confirm is rejected', async () => {
    mockListProviders.mockResolvedValue([providerA])

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '删除 openai-prod' }))
    fireEvent.click(await screen.findByRole('button', { name: '取消' }))

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

    fireEvent.click(await screen.findByLabelText('导入 gpt-4o'))
    const importBtn = screen.getByRole('button', { name: '导入选中' })
    await waitFor(() => expect(importBtn).toBeEnabled())
    fireEvent.click(importBtn)

    await waitFor(() => {
      expect(mockImport).toHaveBeenCalledWith('p1', ['gpt-4o'])
    })
    expect(await screen.findByText('已导入 1 个，跳过 1 个')).toBeInTheDocument()
  })

  it('should show 模型服务不可用 when discovering fails', async () => {
    mockListProviders.mockResolvedValue([providerA])
    mockDiscover.mockRejectedValue(apiError(502, '{"error":"provider service unavailable"}'))

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')

    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))

    expect(await screen.findByText('模型服务不可用')).toBeInTheDocument()
  })

  it('should distinguish discover failures with the shared apiErrorMessage', async () => {
    mockListProviders.mockResolvedValue([providerA])

    mockDiscover.mockRejectedValue(apiError(502, '{"error":"invalid provider response"}'))
    const first = render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')
    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))
    expect(await screen.findByText('模型服务响应非法')).toBeInTheDocument()
    first.unmount()

    mockDiscover.mockRejectedValue(apiError(404, '{"error":"provider not found"}'))
    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod')
    fireEvent.click(screen.getByRole('button', { name: '获取模型 openai-prod' }))
    expect(await screen.findByText('Provider 不存在')).toBeInTheDocument()
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
    const cb2 = (await screen.findByLabelText('导入 gpt-4o')) as HTMLInputElement
    fireEvent.click(cb2)
    await waitFor(() => expect(cb2).toBeChecked())
    const importBtn2 = screen.getByRole('button', { name: '导入选中' })
    await waitFor(() => expect(importBtn2).toBeEnabled())
    fireEvent.click(importBtn2)

    // 导入中：关闭按钮禁用（保住结果反馈）。Modal 右上角关闭按钮同名，取对话框 footer 内的那个。
    const dialog = screen.getByRole('dialog', { name: '获取模型 - openai-prod' })
    const closeBtns = within(dialog).getAllByRole('button', { name: '关闭' })
    expect(closeBtns.some((btn) => (btn as HTMLButtonElement).disabled)).toBe(true)
    expect(dialog).toBeInTheDocument()

    resolveImport({ imported: 1, skipped: 1 })
    expect(await screen.findByText('已导入 1 个，跳过 1 个')).toBeInTheDocument()
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

    render(<ModelSettingsTab />)
    await screen.findByText('openai-prod/gpt-4o')

    // 手动添加：Provider 为下拉选择已有 Providers
    fireEvent.click(screen.getByRole('button', { name: '手动添加模型' }))
    await pickOption('所属 Provider', 'openai-prod')
    fireEvent.change(screen.getByLabelText('模型名'), { target: { value: 'gpt-4o-audio' } })
    fireEvent.click(screen.getByRole('button', { name: '确认添加' }))

    await waitFor(() => {
      expect(mockCreateModel).toHaveBeenCalledWith({
        providerId: 'p1',
        model: 'gpt-4o-audio',
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
    expect((await screen.findAllByText(/确认删除模型/)).length).toBeGreaterThan(0)
    fireEvent.click(screen.getByRole('button', { name: '确认删除' }))
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
    await pickOption('所属 Provider', 'openai-prod')
    fireEvent.change(screen.getByLabelText('模型名'), { target: { value: 'gpt-4o-audio' } })
    const confirm = screen.getByRole('button', { name: '确认添加' })
    fireEvent.click(confirm)
    fireEvent.click(confirm)

    expect(mockCreateModel).toHaveBeenCalledTimes(1)
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /添加中/ })).toBeInTheDocument()
    })

    resolveCreate({ ...modelA, id: 'm3' })
    await waitFor(
      () => {
        expect(screen.queryByRole('dialog', { name: '手动添加模型' })).not.toBeVisible()
      },
      { timeout: 3000 },
    )
  })
})
