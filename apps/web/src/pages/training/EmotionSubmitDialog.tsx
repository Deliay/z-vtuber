import { useEffect, useState } from 'react'
import { Alert, Button, Form, Modal, Select, Typography } from 'antd'
import {
  apiErrorMessage,
  createEmotionTasks,
  listTrainingModels,
  resolveFeaturePointModel,
} from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingModel = components['schemas']['TrainingModel']
type TrainingModelCapability = components['schemas']['TrainingModelCapability']

/** 输入语音识别情绪功能点的标识与能力要求。 */
const FEATURE_POINT_SLUG = 'emotion-from-audio'
const FEATURE_POINT_NAME = '输入语音识别情绪'
const INPUT_CAPABILITIES: TrainingModelCapability[] = ['audio', 'text']
const OUTPUT_CAPABILITIES: TrainingModelCapability[] = ['text']

export interface EmotionSubmitResult {
  created: number
  skipped: number
}

export interface EmotionSubmitDialogProps {
  datasetId: string
  itemIds: string[]
  onClose: () => void
  /** 提交成功回调（用于刷新列表与任务队列）。 */
  onSubmitted?: (result: EmotionSubmitResult) => void
}

/**
 * 批量「情绪识别」确认框：模型必选（slug），确认后提交任务并反馈 created/skipped；取消不发请求。
 */
export function EmotionSubmitDialog({
  datasetId,
  itemIds,
  onClose,
  onSubmitted,
}: EmotionSubmitDialogProps) {
  const [models, setModels] = useState<TrainingModel[]>([])
  const [modelId, setModelId] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<EmotionSubmitResult | null>(null)

  useEffect(() => {
    let active = true
    // resolve 完成功能点自动登记并取回解析结果；候选按功能点能力过滤且仅取启用者。
    Promise.all([
      resolveFeaturePointModel(FEATURE_POINT_SLUG, {
        name: FEATURE_POINT_NAME,
        inputCapabilities: INPUT_CAPABILITIES,
        outputCapabilities: OUTPUT_CAPABILITIES,
      }),
      listTrainingModels(true, INPUT_CAPABILITIES.join(','), OUTPUT_CAPABILITIES.join(',')),
    ])
      .then(([resolved, list]) => {
        if (!active) return
        setModels(list)
        // 默认选中解析结果（specified 优先、default 兜底）；用户仍可临时改选。
        if (resolved.modelId) setModelId(resolved.modelId)
      })
      .catch((err) => {
        if (active) setError(apiErrorMessage(err, '加载模型失败'))
      })
    return () => {
      active = false
    }
  }, [])

  const handleSubmit = async () => {
    if (!modelId || busy) return
    setBusy(true)
    setError('')
    try {
      const created = await createEmotionTasks(datasetId, itemIds, modelId)
      setResult(created)
      onSubmitted?.(created)
    } catch (err) {
      setError(apiErrorMessage(err, '提交情绪识别任务失败'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      title="情绪识别"
      open
      onCancel={onClose}
      mask={{ closable: false }}
      destroyOnHidden
      footer={[
        <Button key="submit" type="primary" loading={busy} disabled={!modelId} onClick={handleSubmit}>
          提交
        </Button>,
        <Button key="cancel" disabled={busy} onClick={onClose}>
          取消
        </Button>,
      ]}
    >
      <Typography.Text>已选择 {itemIds.length} 条数据</Typography.Text>

      <Form layout="vertical" style={{ marginTop: 12 }}>
        <Form.Item label="识别模型">
          <Select
            aria-label="情绪识别模型"
            value={modelId || undefined}
            placeholder="请选择识别模型"
            onChange={setModelId}
            options={models.map((model) => ({ value: model.id as string, label: model.slug as string }))}
          />
        </Form.Item>
      </Form>

      {models.length === 0 && (
        <Alert type="info" showIcon title="请先在模型设置中配置模型与能力" />
      )}
      {error && <Alert type="error" showIcon title={error} />}
      {result && (
        <Alert
          type="success"
          showIcon
          title={`已提交 ${result.created} 条，跳过 ${result.skipped} 条`}
        />
      )}
    </Modal>
  )
}
