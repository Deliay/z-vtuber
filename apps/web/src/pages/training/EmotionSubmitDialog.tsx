import { useEffect, useState } from 'react'
import { Alert, Button, Form, Modal, Select, Typography } from 'antd'
import {
  apiErrorMessage,
  createEmotionTasks,
  listTrainingModels,
} from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingModel = components['schemas']['TrainingModel']

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
    listTrainingModels(true)
      .then((list) => {
        if (active) setModels(list)
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
      maskClosable={false}
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

      {models.length === 0 && <Alert type="info" showIcon message="请先在模型设置中配置" />}
      {error && <Alert type="error" showIcon message={error} />}
      {result && (
        <Alert
          type="success"
          showIcon
          message={`已提交 ${result.created} 条，跳过 ${result.skipped} 条`}
        />
      )}
    </Modal>
  )
}
