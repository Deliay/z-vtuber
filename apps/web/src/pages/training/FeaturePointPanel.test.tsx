import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { FeaturePointPanel } from './FeaturePointPanel'
import * as client from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingFeaturePoint = components['schemas']['TrainingFeaturePoint']
type TrainingModel = components['schemas']['TrainingModel']

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTrainingFeaturePoints: vi.fn(),
    listTrainingModels: vi.fn(),
    setFeaturePointModel: vi.fn(),
    clearFeaturePointModel: vi.fn(),
  }
})

const mockListFeaturePoints = vi.mocked(client.listTrainingFeaturePoints)
const mockListModels = vi.mocked(client.listTrainingModels)
const mockSetModel = vi.mocked(client.setFeaturePointModel)
const mockClearModel = vi.mocked(client.clearFeaturePointModel)

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

const baseFeaturePoint: TrainingFeaturePoint = {
  slug: 'dataset-clean',
  name: '数据清洗',
  inputCapabilities: ['audio', 'text'],
  outputCapabilities: ['text'],
  specifiedModelId: null,
  specifiedModelSlug: null,
  specifiedModelAvailable: false,
  defaultModelId: 'm1',
  defaultModelSlug: 'openai-prod/gpt-4o',
  registeredAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
}

/** 取某功能点所在行，便于对三态与操作列做局部断言。 */
function rowOf(name: string): HTMLElement {
  return screen.getByText(name).closest('tr') as HTMLElement
}

/** 第 5 列为「当前模型」（名称/标识/所需输入/所需输出 之后）。 */
function currentModelCell(row: HTMLElement): HTMLElement {
  return row.querySelectorAll('td')[4] as HTMLElement
}

describe('FeaturePointPanel', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListFeaturePoints.mockResolvedValue([baseFeaturePoint])
    mockListModels.mockResolvedValue([modelA])
  })

  it('should query candidates with each feature point required capabilities and only enabled ones', async () => {
    render(<FeaturePointPanel />)

    await screen.findByText('数据清洗')

    expect(mockListModels).toHaveBeenCalledWith(true, 'audio,text', 'text')
    // 列头与能力中文标签
    expect(screen.getByRole('columnheader', { name: '所需输入能力' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: '所需输出能力' })).toBeInTheDocument()
    const row = rowOf('数据清洗')
    expect(within(row).getAllByText('语音、文本')).toHaveLength(1)
    expect(within(row).getAllByText('文本')).toHaveLength(1)
  })

  it('should show the default model slug when no model is specified', async () => {
    render(<FeaturePointPanel />)

    await screen.findByText('数据清洗')
    expect(currentModelCell(rowOf('数据清洗'))).toHaveTextContent('openai-prod/gpt-4o')
  })

  it('should show 无可用模型 when there is neither a specified nor a default model', async () => {
    mockListFeaturePoints.mockResolvedValue([
      { ...baseFeaturePoint, defaultModelId: null, defaultModelSlug: null },
    ])

    render(<FeaturePointPanel />)

    await screen.findByText('数据清洗')
    expect(within(rowOf('数据清洗')).getByText('无可用模型')).toBeInTheDocument()
  })

  it('should show the specified model slug when it is available', async () => {
    mockListFeaturePoints.mockResolvedValue([
      {
        ...baseFeaturePoint,
        specifiedModelId: 'm2',
        specifiedModelSlug: 'claude-prod/sonnet',
        specifiedModelAvailable: true,
      },
    ])

    render(<FeaturePointPanel />)

    await screen.findByText('数据清洗')
    const row = rowOf('数据清洗')
    expect(within(row).getByText('claude-prod/sonnet')).toBeInTheDocument()
    expect(within(row).queryByText('指定模型不可用')).not.toBeInTheDocument()
  })

  it('should flag an unavailable specified model and still expose the default fallback', async () => {
    mockListFeaturePoints.mockResolvedValue([
      {
        ...baseFeaturePoint,
        specifiedModelId: 'm9',
        specifiedModelSlug: 'gone/model',
        specifiedModelAvailable: false,
      },
    ])

    render(<FeaturePointPanel />)

    await screen.findByText('数据清洗')
    const cell = currentModelCell(rowOf('数据清洗'))
    expect(cell).toHaveTextContent('gone/model')
    expect(cell).toHaveTextContent('指定模型不可用')
    expect(cell).toHaveTextContent('openai-prod/gpt-4o')
  })

  it('should specify a model and reload the list', async () => {
    mockSetModel.mockResolvedValue({
      ...baseFeaturePoint,
      specifiedModelId: 'm1',
      specifiedModelSlug: 'openai-prod/gpt-4o',
      specifiedModelAvailable: true,
    })
    render(<FeaturePointPanel />)
    await screen.findByText('数据清洗')

    fireEvent.change(screen.getByLabelText('指定模型 dataset-clean'), {
      target: { value: 'm1' },
    })
    fireEvent.click(screen.getByRole('button', { name: '指定 dataset-clean' }))

    await waitFor(() => {
      expect(mockSetModel).toHaveBeenCalledWith('dataset-clean', 'm1')
    })
    await waitFor(() => {
      expect(mockListFeaturePoints).toHaveBeenCalledTimes(2)
    })
  })

  it('should only offer 恢复默认 when a model is specified and clear it', async () => {
    mockListFeaturePoints.mockResolvedValue([
      {
        ...baseFeaturePoint,
        specifiedModelId: 'm1',
        specifiedModelSlug: 'openai-prod/gpt-4o',
        specifiedModelAvailable: true,
      },
    ])
    mockClearModel.mockResolvedValue(baseFeaturePoint)

    render(<FeaturePointPanel />)
    await screen.findByText('数据清洗')

    fireEvent.click(screen.getByRole('button', { name: '恢复默认 dataset-clean' }))

    await waitFor(() => {
      expect(mockClearModel).toHaveBeenCalledWith('dataset-clean')
    })
  })

  it('should hide 恢复默认 when nothing is specified', async () => {
    render(<FeaturePointPanel />)
    await screen.findByText('数据清洗')

    expect(
      screen.queryByRole('button', { name: '恢复默认 dataset-clean' }),
    ).not.toBeInTheDocument()
  })

  it('should guide to configure models and capabilities when there is no candidate', async () => {
    mockListModels.mockResolvedValue([])

    render(<FeaturePointPanel />)

    expect(
      await screen.findByText('该功能点暂无可用模型，请先在模型设置中配置模型与能力'),
    ).toBeInTheDocument()
    expect(screen.queryByLabelText('指定模型 dataset-clean')).not.toBeInTheDocument()
  })

  it('should surface a load failure through the shared apiErrorMessage without bubbling', async () => {
    mockListFeaturePoints.mockRejectedValue(
      Object.assign(new Error('API request failed'), {
        status: 500,
        serverMessage: '{"error":"feature point not found"}',
      }),
    )

    render(<FeaturePointPanel />)

    expect(await screen.findByRole('alert')).toHaveTextContent('功能点不存在')
  })
})
