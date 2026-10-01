import { useCallback, useEffect, useState } from 'react'
import {
  Alert,
  App as AntdApp,
  Button,
  Checkbox,
  Empty,
  Form,
  Input,
  InputNumber,
  Select,
  Space,
  Table,
  Tag,
  Typography,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  getTranscriptSettings,
  setRoomAutoEnqueue,
  listTranscriptModels,
  createTranscriptModel,
  updateTranscriptModel,
  getTranscriptCandidateRooms,
  deleteTranscriptSettings,
} from '../../api/client'
import type { components as transcriptComponents } from '../../api/schema-recording-transcript'

type TranscriptSettingsItem = transcriptComponents['schemas']['TranscriptSettingsItem']
type TranscriptModel = transcriptComponents['schemas']['TranscriptModel']
type CandidateRoom = transcriptComponents['schemas']['CandidateRoom']

function normalizeConcurrency(value: string | number | null | undefined): number {
  const parsed = Number(value)
  if (!Number.isFinite(parsed) || parsed < 1) {
    return 1
  }
  return Math.floor(parsed)
}

export function SettingsTab() {
  const { message, modal } = AntdApp.useApp()
  const [settings, setSettings] = useState<TranscriptSettingsItem[]>([])
  const [models, setModels] = useState<TranscriptModel[]>([])
  const [candidates, setCandidates] = useState<CandidateRoom[]>([])
  const [loading, setLoading] = useState(true)
  const [initialized, setInitialized] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [retryAction, setRetryAction] = useState<(() => void) | null>(null)

  const [newRoomId, setNewRoomId] = useState('')
  const [newRoomModelId, setNewRoomModelId] = useState('')
  const [adding, setAdding] = useState(false)

  const [modelName, setModelName] = useState('')
  const [modelApiKey, setModelApiKey] = useState('')
  const [modelIdentifier, setModelIdentifier] = useState('')
  const [modelMaxConcurrency, setModelMaxConcurrency] = useState('1')
  const [modelEnabled, setModelEnabled] = useState(true)
  const [creatingModel, setCreatingModel] = useState(false)
  const [concurrencyDrafts, setConcurrencyDrafts] = useState<Record<string, string>>({})
  const [bindingDrafts, setBindingDrafts] = useState<Record<number, string>>({})

  const fetchAll = useCallback(async () => {
    try {
      setError(null)
      setRetryAction(null)
      setLoading(true)
      const [settingsData, modelsData, candidatesData] = await Promise.all([
        getTranscriptSettings(),
        listTranscriptModels(),
        getTranscriptCandidateRooms(),
      ])
      setSettings(settingsData)
      setModels(modelsData)
      setCandidates(candidatesData ?? [])
    } catch {
      setError('服务不可用')
      setRetryAction(null)
    } finally {
      setLoading(false)
      setInitialized(true)
    }
  }, [])

  useEffect(() => {
    fetchAll()
  }, [fetchAll])

  const enabledModels = models.filter((model) => model.enabled === true)

  const handleToggleAuto = async (item: TranscriptSettingsItem) => {
    if (typeof item.roomId !== 'number') {
      return
    }

    const targetModelId = item.modelId ?? bindingDrafts[item.roomId]
    if (!item.enabled && !targetModelId) {
      message.error('请先选择识别模型，自动转写需要绑定模型')
      return
    }

    try {
      setError(null)
      setRetryAction(null)
      if (item.enabled) {
        await setRoomAutoEnqueue(item.roomId, false)
      } else {
        await setRoomAutoEnqueue(item.roomId, true, targetModelId ?? undefined)
      }
      message.success(item.enabled ? '已关闭自动识别' : '已开启自动识别')
      await fetchAll()
    } catch {
      setError('自动识别设置更新失败')
      setRetryAction(() => () => {
        void handleToggleAuto(item)
      })
    }
  }

  const performDelete = async (roomId: number) => {
    try {
      setError(null)
      setRetryAction(null)
      await deleteTranscriptSettings(roomId)
      message.success('自动识别设置已删除')
      await fetchAll()
    } catch {
      setError('删除自动识别设置失败')
      setRetryAction(() => () => {
        void performDelete(roomId)
      })
    }
  }

  const handleDelete = (item: TranscriptSettingsItem) => {
    if (typeof item.roomId !== 'number') {
      return
    }
    modal.confirm({
      title: `确认删除直播间 ${item.roomId} 的自动识别设置？`,
      content: '删除后该直播间不再自动发起转写。',
      okText: '确认删除',
      cancelText: '返回',
      okButtonProps: { danger: true },
      onOk: () => performDelete(item.roomId as number),
    })
  }

  const handleAddAuto = async () => {
    const roomId = Number(newRoomId)
    if (!Number.isInteger(roomId) || roomId <= 0) {
      message.error('请输入有效的直播间号')
      return
    }
    if (!newRoomModelId) {
      message.error('请先选择识别模型')
      return
    }

    setAdding(true)
    try {
      setError(null)
      setRetryAction(null)
      await setRoomAutoEnqueue(roomId, true, newRoomModelId)
      setNewRoomId('')
      setNewRoomModelId('')
      message.success('已添加自动识别')
      await fetchAll()
    } catch {
      setError('添加自动识别失败')
      setRetryAction(() => () => {
        void handleAddAuto()
      })
    } finally {
      setAdding(false)
    }
  }

  const retryFailedAction = () => {
    const action = retryAction ?? fetchAll
    setError(null)
    setRetryAction(null)
    void action()
  }

  const handleCreateModel = async () => {
    if (!modelName.trim()) {
      message.error('请输入模型名称')
      return
    }
    if (!modelIdentifier.trim()) {
      message.error('请输入识别模型')
      return
    }
    if (!modelApiKey.trim()) {
      message.error('请输入 API Key')
      return
    }

    setCreatingModel(true)
    try {
      await createTranscriptModel({
        name: modelName,
        provider: 'ali-fun-asr',
        type: 'streaming',
        apiKey: modelApiKey,
        model: modelIdentifier,
        enabled: modelEnabled,
        maxConcurrency: normalizeConcurrency(modelMaxConcurrency),
      })
      setModelName('')
      setModelApiKey('')
      setModelIdentifier('')
      setModelMaxConcurrency('1')
      setModelEnabled(true)
      message.success('模型已添加')
      const modelsData = await listTranscriptModels()
      setModels(modelsData)
    } catch {
      message.error('添加模型失败')
    } finally {
      setCreatingModel(false)
    }
  }

  const handleUpdateModel = async (
    model: TranscriptModel,
    patch: transcriptComponents['schemas']['TranscriptModelUpdateRequest'],
  ) => {
    if (!model.id) {
      return
    }
    try {
      await updateTranscriptModel(model.id, patch)
      const modelsData = await listTranscriptModels()
      setModels(modelsData)
    } catch {
      message.error('模型更新失败')
    }
  }

  const settingsColumns: ColumnsType<TranscriptSettingsItem> = [
    {
      title: '主播',
      key: 'anchor',
      render: (_value, item) => `${item.anchorName ?? '-'}（${item.roomId}）`,
    },
    {
      title: '识别模型',
      key: 'model',
      render: (_value, item) =>
        item.modelId ? (
          (item.modelName ?? item.modelId)
        ) : (
          <Space size={8}>
            <Tag color="warning">待完善</Tag>
            <Select
              aria-label={`识别模型-${item.roomId}`}
              value={bindingDrafts[item.roomId] ?? undefined}
              placeholder="选择识别模型"
              style={{ width: 180 }}
              onChange={(value) =>
                setBindingDrafts((prev) => ({ ...prev, [item.roomId as number]: value }))
              }
              options={enabledModels.map((model) => ({
                value: model.id as string,
                label: model.name as string,
              }))}
            />
          </Space>
        ),
    },
    {
      title: '自动识别',
      key: 'enabled',
      width: 100,
      render: (_value, item) => (item.enabled ? '已开启' : '已关闭'),
    },
    {
      title: '操作',
      key: 'action',
      width: 180,
      render: (_value, item) => (
        <Space size={8}>
          <Button onClick={() => handleToggleAuto(item)}>
            {item.enabled ? '关闭' : '开启'}
          </Button>
          {typeof item.roomId === 'number' && (
            <Button
              danger
              aria-label={`删除自动识别-${item.roomId}`}
              onClick={() => handleDelete(item)}
            >
              删除
            </Button>
          )}
        </Space>
      ),
    },
  ]

  const modelColumns: ColumnsType<TranscriptModel> = [
    { title: '名称', dataIndex: 'name' },
    { title: '识别模型', dataIndex: 'model', render: (value) => value ?? '-' },
    { title: '提供方', dataIndex: 'provider' },
    {
      title: '状态',
      key: 'enabled',
      width: 100,
      render: (_value, model) => (model.enabled ? '已启用' : '已禁用'),
    },
    {
      title: '最大并发',
      key: 'concurrency',
      width: 200,
      render: (_value, model) => (
        <Space size={8}>
          <InputNumber
            aria-label={`最大并发-${model.name}`}
            min={1}
            value={
              concurrencyDrafts[model.id ?? ''] !== undefined
                ? Number(concurrencyDrafts[model.id ?? ''])
                : (model.maxConcurrency ?? 1)
            }
            onChange={(value) =>
              setConcurrencyDrafts((prev) => ({
                ...prev,
                [model.id ?? '']: String(value ?? ''),
              }))
            }
            style={{ width: 80 }}
          />
          <Button
            onClick={() =>
              handleUpdateModel(model, {
                maxConcurrency: normalizeConcurrency(
                  concurrencyDrafts[model.id ?? ''] ?? model.maxConcurrency,
                ),
              })
            }
          >
            保存
          </Button>
        </Space>
      ),
    },
    {
      title: '操作',
      key: 'action',
      width: 100,
      render: (_value, model) => (
        <Button onClick={() => handleUpdateModel(model, { enabled: !model.enabled })}>
          {model.enabled ? '禁用' : '启用'}
        </Button>
      ),
    },
  ]

  if (loading && !initialized) {
    return (
      <div style={{ textAlign: 'center', padding: 32 }}>
        <Typography.Text type="secondary">加载中...</Typography.Text>
      </div>
    )
  }

  return (
    <div>
      {error && (
        <Alert
          type="error"
          showIcon
          title={error}
          style={{ marginBottom: 16 }}
          action={<Button onClick={retryFailedAction}>重试</Button>}
        />
      )}

      <Typography.Title level={4}>识别设置</Typography.Title>

      <Table<TranscriptSettingsItem>
        rowKey={(item) => String(item.roomId)}
        columns={settingsColumns}
        dataSource={settings}
        pagination={false}
        locale={{ emptyText: <Empty description="暂无自动识别配置" /> }}
        style={{ marginBottom: 16 }}
      />

      <Space style={{ marginBottom: 8 }} wrap>
        <Select
          aria-label="直播间号"
          placeholder="选择直播间"
          value={newRoomId || undefined}
          onChange={setNewRoomId}
          style={{ width: 200 }}
          options={candidates.map((candidate) => ({
            value: String(candidate.roomId),
            label: candidate.anchorName
              ? `${candidate.anchorName}（${candidate.roomId}）`
              : `${candidate.roomId}`,
          }))}
        />
        <Select
          aria-label="添加识别模型"
          placeholder="选择识别模型"
          value={newRoomModelId || undefined}
          onChange={setNewRoomModelId}
          style={{ width: 180 }}
          options={enabledModels.map((model) => ({
            value: model.id as string,
            label: model.name as string,
          }))}
        />
        <Button
          type="primary"
          disabled={candidates.length === 0}
          loading={adding}
          onClick={handleAddAuto}
        >
          添加自动识别
        </Button>
      </Space>
      {candidates.length === 0 && (
        <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 16 }}>
          暂无可选主播
        </Typography.Text>
      )}

      <Typography.Title level={4} style={{ marginTop: 24 }}>
        模型设置
      </Typography.Title>

      <Table<TranscriptModel>
        rowKey={(model) => model.id ?? ''}
        columns={modelColumns}
        dataSource={models}
        pagination={false}
        locale={{ emptyText: <Empty description="暂无模型" /> }}
        style={{ marginBottom: 16 }}
      />

      <Typography.Title level={5}>添加模型</Typography.Title>
      <Form layout="inline" style={{ rowGap: 12 }}>
        <Form.Item style={{ marginBottom: 0 }}>
          <Input
            aria-label="模型名称"
            placeholder="模型名称"
            value={modelName}
            onChange={(e) => setModelName(e.target.value)}
            style={{ width: 160 }}
          />
        </Form.Item>
        <Form.Item style={{ marginBottom: 0 }}>
          <Input
            aria-label="识别模型"
            placeholder="识别模型（如 fun-asr-realtime）"
            value={modelIdentifier}
            onChange={(e) => setModelIdentifier(e.target.value)}
            style={{ width: 220 }}
          />
        </Form.Item>
        <Form.Item style={{ marginBottom: 0 }}>
          <Input.Password
            aria-label="API Key"
            placeholder="API Key"
            value={modelApiKey}
            onChange={(e) => setModelApiKey(e.target.value)}
            style={{ width: 180 }}
          />
        </Form.Item>
        <Form.Item style={{ marginBottom: 0 }}>
          <InputNumber
            aria-label="最大并发数"
            min={1}
            value={Number(modelMaxConcurrency)}
            onChange={(value) => setModelMaxConcurrency(String(value ?? 1))}
            style={{ width: 100 }}
          />
        </Form.Item>
        <Form.Item style={{ marginBottom: 0 }}>
          <Checkbox checked={modelEnabled} onChange={(e) => setModelEnabled(e.target.checked)}>
            启用
          </Checkbox>
        </Form.Item>
        <Form.Item style={{ marginBottom: 0 }}>
          <Button type="primary" loading={creatingModel} onClick={handleCreateModel}>
            添加
          </Button>
        </Form.Item>
      </Form>
    </div>
  )
}
