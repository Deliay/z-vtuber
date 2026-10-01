import { useCallback, useEffect, useState } from 'react'
import {
  Alert,
  App as AntdApp,
  Button,
  Checkbox,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Table,
  Typography,
} from 'antd'
import type { TableColumnsType } from 'antd'
import {
  apiErrorMessage,
  createTrainingModel,
  deleteTrainingModel,
  deleteTrainingProvider,
  discoverTrainingModels,
  importTrainingModels,
  listTrainingModels,
  listTrainingProviders,
  updateTrainingModel,
} from '../../api/client'
import { ProviderDialog } from './ProviderDialog'
import { FeaturePointPanel } from './FeaturePointPanel'
import { TruncatedText } from '../../components/TruncatedText'
import type { components } from '../../api/schema-training'

type TrainingProvider = components['schemas']['TrainingProvider']
type TrainingModel = components['schemas']['TrainingModel']
type TrainingModelCapability = components['schemas']['TrainingModelCapability']

const CAPABILITIES: TrainingModelCapability[] = ['audio', 'video', 'text', 'image']

const CAPABILITY_LABELS: Record<TrainingModelCapability, string> = {
  audio: '语音',
  video: '视频',
  text: '文本',
  image: '图片',
}

/** 能力集合 → 中文标签（空集合显示「未设置」）。 */
function capabilityLabels(capabilities: string[]): string {
  if (capabilities.length === 0) return '未设置'
  return capabilities.map((cap) => CAPABILITY_LABELS[cap as TrainingModelCapability] ?? cap).join('、')
}

/** 四值能力勾选组（输入/输出共用）。 */
function CapabilityCheckboxes({
  legend,
  selected,
  onChange,
}: {
  legend: string
  selected: TrainingModelCapability[]
  onChange: (next: TrainingModelCapability[]) => void
}) {
  return (
    <div style={{ marginBottom: 8 }}>
      <Typography.Text strong style={{ display: 'block', marginBottom: 4 }}>
        {legend}
      </Typography.Text>
      <Space size={12} wrap>
        {CAPABILITIES.map((cap) => (
          <Checkbox
            key={cap}
            aria-label={`${legend} ${CAPABILITY_LABELS[cap]}`}
            // 不用 Checkbox.Group：其 onChange 会经 registeredValues 过滤，
            // 子项注册在 effect 中、首次勾选会被吞；此处显式维护选中集。
            checked={selected.includes(cap)}
            onChange={(event) =>
              onChange(
                event.target.checked
                  ? [...selected, cap]
                  : selected.filter((value) => value !== cap),
              )
            }
          >
            {CAPABILITY_LABELS[cap]}
          </Checkbox>
        ))}
      </Space>
    </div>
  )
}

/**
 * 模型设置：Providers 双表管理（新增/编辑/删除/获取模型导入）+ Models 管理
 * （slug 展示、手动添加、启停、删除）。API key 只进不出、永不回显。
 */
export function ModelSettingsTab() {
  const { message, modal } = AntdApp.useApp()
  const [providers, setProviders] = useState<TrainingProvider[]>([])
  const [models, setModels] = useState<TrainingModel[]>([])
  const [error, setError] = useState('')

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingProvider, setEditingProvider] = useState<TrainingProvider | undefined>(undefined)

  const [discoverTarget, setDiscoverTarget] = useState<TrainingProvider | null>(null)
  const [discovered, setDiscovered] = useState<string[]>([])
  const [discoverSelected, setDiscoverSelected] = useState<string[]>([])
  const [discoverBusy, setDiscoverBusy] = useState(false)
  /** 导入选中模型请求进行中：此时忽略遮罩关闭并禁用关闭按钮，保住结果反馈。 */
  const [discoverImporting, setDiscoverImporting] = useState(false)
  const [discoverError, setDiscoverError] = useState('')
  const [discoverResult, setDiscoverResult] = useState<{ imported: number; skipped: number } | null>(null)

  const [manualOpen, setManualOpen] = useState(false)
  const [manualProviderId, setManualProviderId] = useState('')
  const [manualModel, setManualModel] = useState('')
  const [manualInputCapabilities, setManualInputCapabilities] = useState<TrainingModelCapability[]>([])
  const [manualOutputCapabilities, setManualOutputCapabilities] = useState<TrainingModelCapability[]>([])
  const [manualError, setManualError] = useState('')
  /** 手动添加模型进行中（防双击重复提交；请求途中忽略遮罩关闭）。 */
  const [manualSubmitting, setManualSubmitting] = useState(false)

  const [capabilityTarget, setCapabilityTarget] = useState<TrainingModel | null>(null)
  const [capabilityInput, setCapabilityInput] = useState<TrainingModelCapability[]>([])
  const [capabilityOutput, setCapabilityOutput] = useState<TrainingModelCapability[]>([])
  const [capabilityError, setCapabilityError] = useState('')
  const [capabilitySubmitting, setCapabilitySubmitting] = useState(false)

  const refresh = useCallback(async () => {
    try {
      const [providerList, modelList] = await Promise.all([
        listTrainingProviders(),
        listTrainingModels(),
      ])
      setProviders(providerList)
      setModels(modelList)
      setError('')
    } catch (err) {
      setError(apiErrorMessage(err, '加载模型设置失败'))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const openCreate = () => {
    setEditingProvider(undefined)
    setDialogOpen(true)
  }

  const openEdit = (provider: TrainingProvider) => {
    setEditingProvider(provider)
    setDialogOpen(true)
  }

  const handleDeleteProvider = (provider: TrainingProvider) => {
    const childCount = models.filter((model) => model.providerId === provider.id).length
    modal.confirm({
      title: `确认删除 Provider「${provider.name}」？`,
      content: `其下 ${childCount} 个模型将一并删除，不可恢复。`,
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          await deleteTrainingProvider(provider.id)
          message.success('Provider 已删除')
          await refresh()
        } catch (err) {
          setError(apiErrorMessage(err, '删除 Provider 失败'))
        }
      },
    })
  }

  const handleDiscover = async (provider: TrainingProvider) => {
    setDiscoverTarget(provider)
    setDiscovered([])
    setDiscoverSelected([])
    setDiscoverError('')
    setDiscoverResult(null)
    setDiscoverImporting(false)
    setDiscoverBusy(true)
    try {
      const list = await discoverTrainingModels(provider.id)
      setDiscovered(list.map((item) => item.model as string))
    } catch (err) {
      // 保留服务端错误码（不可达 / 响应非法 / Provider 不存在 等需区分）。
      setDiscoverError(apiErrorMessage(err, '模型服务不可用'))
    } finally {
      setDiscoverBusy(false)
    }
  }

  const handleImport = async () => {
    if (!discoverTarget || discoverSelected.length === 0 || discoverBusy) return
    setDiscoverBusy(true)
    setDiscoverImporting(true)
    setDiscoverError('')
    try {
      const result = await importTrainingModels(discoverTarget.id, discoverSelected)
      setDiscoverResult(result)
      await refresh()
    } catch (err) {
      setDiscoverError(apiErrorMessage(err, '导入模型失败'))
    } finally {
      setDiscoverBusy(false)
      setDiscoverImporting(false)
    }
  }

  const handleToggleModel = async (model: TrainingModel) => {
    try {
      await updateTrainingModel(model.id, { enabled: !model.enabled })
      await refresh()
    } catch (err) {
      setError(apiErrorMessage(err, '更新模型失败'))
    }
  }

  const handleDeleteModel = (model: TrainingModel) => {
    modal.confirm({
      title: `确认删除模型「${model.slug}」？`,
      content: '删除后不可恢复，相关任务将不再可选该模型。',
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          await deleteTrainingModel(model.id)
          message.success('模型已删除')
          await refresh()
        } catch (err) {
          setError(apiErrorMessage(err, '删除模型失败'))
        }
      },
    })
  }

  const handleCreateModel = async () => {
    if (manualSubmitting) return
    if (!manualProviderId) {
      setManualError('请选择 Provider')
      return
    }
    setManualSubmitting(true)
    try {
      await createTrainingModel({
        providerId: manualProviderId,
        model: manualModel.trim(),
        inputCapabilities: manualInputCapabilities,
        outputCapabilities: manualOutputCapabilities,
      })
      setManualOpen(false)
      setManualModel('')
      setManualProviderId('')
      setManualInputCapabilities([])
      setManualOutputCapabilities([])
      setManualError('')
      message.success('模型已添加')
      await refresh()
    } catch (err) {
      setManualError(apiErrorMessage(err, '添加模型失败'))
    } finally {
      setManualSubmitting(false)
    }
  }

  const openCapabilityEdit = (model: TrainingModel) => {
    setCapabilityTarget(model)
    setCapabilityInput(model.inputCapabilities)
    setCapabilityOutput(model.outputCapabilities)
    setCapabilityError('')
  }

  const handleSaveCapabilities = async () => {
    if (!capabilityTarget || capabilitySubmitting) return
    setCapabilitySubmitting(true)
    setCapabilityError('')
    try {
      // 空数组表示清空能力，缺省才不改；此处始终显式提交两组能力。
      await updateTrainingModel(capabilityTarget.id, {
        inputCapabilities: capabilityInput,
        outputCapabilities: capabilityOutput,
      })
      setCapabilityTarget(null)
      await refresh()
    } catch (err) {
      setCapabilityError(apiErrorMessage(err, '保存能力失败'))
    } finally {
      setCapabilitySubmitting(false)
    }
  }

  const providerColumns: TableColumnsType<TrainingProvider> = [
    { title: '名称', dataIndex: 'name' },
    { title: '模型类型', dataIndex: 'type' },
    {
      title: 'Endpoint',
      dataIndex: 'endpoint',
      render: (value) => <TruncatedText text={String(value ?? '-')} />,
    },
    {
      title: 'API key 状态',
      key: 'apiKeySet',
      width: 130,
      render: (_value, provider) => (provider.apiKeySet ? '已配置' : '未配置'),
    },
    {
      title: '操作',
      key: 'action',
      width: 280,
      render: (_value, provider) => (
        <Space size={8}>
          <Button size="small" aria-label={`获取模型 ${provider.name}`} onClick={() => void handleDiscover(provider)}>
            获取模型
          </Button>
          <Button size="small" aria-label={`编辑 ${provider.name}`} onClick={() => openEdit(provider)}>
            编辑
          </Button>
          <Button size="small" danger aria-label={`删除 ${provider.name}`} onClick={() => handleDeleteProvider(provider)}>
            删除
          </Button>
        </Space>
      ),
    },
  ]

  const modelColumns: TableColumnsType<TrainingModel> = [
    { title: 'slug', dataIndex: 'slug' },
    { title: '所属 Provider', dataIndex: 'providerName' },
    {
      title: '输入能力',
      key: 'inputCapabilities',
      render: (_value, model) =>
        model.inputCapabilities.length === 0 ? (
          <Typography.Text type="secondary">未设置</Typography.Text>
        ) : (
          capabilityLabels(model.inputCapabilities)
        ),
    },
    {
      title: '输出能力',
      key: 'outputCapabilities',
      render: (_value, model) =>
        model.outputCapabilities.length === 0 ? (
          <Typography.Text type="secondary">未设置</Typography.Text>
        ) : (
          capabilityLabels(model.outputCapabilities)
        ),
    },
    {
      title: '启用',
      key: 'enabled',
      width: 100,
      render: (_value, model) => (model.enabled ? '已启用' : '已停用'),
    },
    {
      title: '操作',
      key: 'action',
      width: 260,
      render: (_value, model) => (
        <Space size={8}>
          <Button
            size="small"
            aria-label={`${model.enabled ? '停用' : '启用'} ${model.slug}`}
            onClick={() => void handleToggleModel(model)}
          >
            {model.enabled ? '停用' : '启用'}
          </Button>
          <Button
            size="small"
            aria-label={`编辑能力 ${model.slug}`}
            onClick={() => openCapabilityEdit(model)}
          >
            编辑能力
          </Button>
          <Button
            size="small"
            danger
            aria-label={`删除 ${model.slug}`}
            onClick={() => void handleDeleteModel(model)}
          >
            删除
          </Button>
        </Space>
      ),
    },
  ]

  return (
    <div className="model-settings-tab">
      {error && <Alert type="error" showIcon title={error} style={{ marginBottom: 16 }} />}

      <Typography.Title level={4}>Providers</Typography.Title>
      <Button onClick={openCreate} style={{ marginBottom: 12 }}>
        新增 Provider
      </Button>

      {providers.length === 0 && (
        <Typography.Text type="secondary">请先添加 Provider 并获取/添加模型</Typography.Text>
      )}

      {providers.length > 0 && (
        <Table<TrainingProvider>
          rowKey={(provider) => provider.id}
          columns={providerColumns}
          dataSource={providers}
          pagination={false}
          style={{ marginBottom: 24 }}
        />
      )}

      <Typography.Title level={4}>Models</Typography.Title>
      <Button onClick={() => setManualOpen(true)} style={{ marginBottom: 12 }}>
        手动添加模型
      </Button>

      {/* Provider 已存在但尚无模型时同样给出引导（最常见的中间态）。 */}
      {models.length === 0 && (
        <Typography.Text type="secondary">暂无模型，请通过「获取模型」导入或手动添加</Typography.Text>
      )}

      {models.length > 0 && (
        <Table<TrainingModel>
          rowKey={(model) => model.id}
          columns={modelColumns}
          dataSource={models}
          pagination={false}
        />
      )}

      <FeaturePointPanel />

      {dialogOpen && (
        <ProviderDialog
          provider={editingProvider}
          onClose={() => setDialogOpen(false)}
          onSaved={() => void refresh()}
        />
      )}

      <Modal
        title={`获取模型 - ${discoverTarget?.name ?? ''}`}
        open={discoverTarget !== null}
        onCancel={() => {
          if (!discoverImporting) setDiscoverTarget(null)
        }}
        mask={{ closable: !discoverImporting }}
        destroyOnHidden
        footer={[
          <Button
            key="import"
            type="primary"
            disabled={discoverSelected.length === 0 || discoverBusy}
            loading={discoverImporting}
            onClick={() => void handleImport()}
          >
            导入选中
          </Button>,
          <Button key="close" disabled={discoverImporting} onClick={() => setDiscoverTarget(null)}>
            关闭
          </Button>,
        ]}
      >
        {discoverBusy && discovered.length === 0 && <Typography.Text type="secondary">加载中...</Typography.Text>}
        {discoverError && <Alert type="error" showIcon title={discoverError} />}

        {discovered.length > 0 && (
          <Space orientation="vertical">
            {discovered.map((model) => (
              <Checkbox
                key={model}
                aria-label={`导入 ${model}`}
                // 不用 Checkbox.Group：其 onChange 会经 registeredValues 过滤，
                // 而子项注册在 effect 中、异步 discovery 场景下首次勾选会被吞。
                checked={discoverSelected.includes(model)}
                onChange={(event) =>
                  setDiscoverSelected((prev) =>
                    event.target.checked
                      ? [...prev, model]
                      : prev.filter((value) => value !== model),
                  )
                }
              >
                {model}
              </Checkbox>
            ))}
          </Space>
        )}

        {!discoverBusy && !discoverError && discovered.length === 0 && (
          <Typography.Text type="secondary">未获取到模型</Typography.Text>
        )}

        {discoverResult && (
          <Alert
            type="success"
            showIcon
            title={`已导入 ${discoverResult.imported} 个，跳过 ${discoverResult.skipped} 个`}
          />
        )}
      </Modal>

      <Modal
        title="手动添加模型"
        open={manualOpen}
        onCancel={() => {
          if (!manualSubmitting) setManualOpen(false)
        }}
        mask={{ closable: !manualSubmitting }}
        destroyOnHidden
        footer={[
          <Button key="ok" type="primary" loading={manualSubmitting} onClick={() => void handleCreateModel()}>
            {manualSubmitting ? '添加中...' : '确认添加'}
          </Button>,
          <Button key="cancel" disabled={manualSubmitting} onClick={() => setManualOpen(false)}>
            取消
          </Button>,
        ]}
      >
        <Form layout="vertical">
          <Form.Item label="所属 Provider">
            <Select
              aria-label="所属 Provider"
              value={manualProviderId || undefined}
              placeholder="请选择 Provider"
              onChange={setManualProviderId}
              options={providers.map((provider) => ({
                value: provider.id,
                label: provider.name as string,
              }))}
            />
          </Form.Item>
          <Form.Item label="模型名">
            <Input aria-label="模型名" value={manualModel} onChange={(e) => setManualModel(e.target.value)} />
          </Form.Item>
        </Form>

        <CapabilityCheckboxes
          legend="输入能力"
          selected={manualInputCapabilities}
          onChange={setManualInputCapabilities}
        />
        <CapabilityCheckboxes
          legend="输出能力"
          selected={manualOutputCapabilities}
          onChange={setManualOutputCapabilities}
        />

        {manualError && <Alert type="error" showIcon title={manualError} />}
      </Modal>

      <Modal
        title={`编辑能力 ${capabilityTarget?.slug ?? ''}`}
        open={capabilityTarget !== null}
        onCancel={() => {
          if (!capabilitySubmitting) setCapabilityTarget(null)
        }}
        mask={{ closable: !capabilitySubmitting }}
        destroyOnHidden
        footer={[
          <Button
            key="save"
            type="primary"
            loading={capabilitySubmitting}
            onClick={() => void handleSaveCapabilities()}
          >
            {capabilitySubmitting ? '保存中...' : '保存'}
          </Button>,
          <Button
            key="cancel"
            disabled={capabilitySubmitting}
            onClick={() => setCapabilityTarget(null)}
          >
            取消
          </Button>,
        ]}
      >
        <CapabilityCheckboxes
          legend="输入能力"
          selected={capabilityInput}
          onChange={setCapabilityInput}
        />
        <CapabilityCheckboxes
          legend="输出能力"
          selected={capabilityOutput}
          onChange={setCapabilityOutput}
        />

        {capabilityError && <Alert type="error" showIcon title={capabilityError} />}
      </Modal>
    </div>
  )
}
