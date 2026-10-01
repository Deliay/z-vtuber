import { useEffect, useState } from 'react'
import { Alert, Button, Form, Input, Modal, Select } from 'antd'
import {
  apiErrorMessage,
  listTrainingModels,
  recognizeTrainingDatasetItem,
  updateTrainingDatasetItem,
} from '../../api/client'
import { parsePosition } from '../../utils/format'
import type { components } from '../../api/schema-training'

type TrainingModel = components['schemas']['TrainingModel']
type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']

/** 内联编辑草稿（`m:ss`/`m:ss.S` 字符串口径），保存时与识别文本一并提交，避免静默丢弃。 */
export interface ReRecognizeDialogDraft {
  audioUri: string
  text: string
  start: string
  end: string
}

export interface ReRecognizeDialogItem {
  id: string
  text: string
}

export interface ReRecognizeDialogProps {
  datasetId: string
  item: ReRecognizeDialogItem
  /** 该行内联编辑草稿：传入则「保存」与内联保存同 payload（audioUri/startMs/endMs + text）。 */
  draft?: ReRecognizeDialogDraft
  onClose: () => void
  /** 保存成功后回调（携带更新后的条目）。 */
  onSaved: (updated: TrainingDatasetItem) => void
}

const TIME_INVALID = '时间输入无效'

/**
 * 单条「重新识别」对话框：选择已启用模型 → 识别回填「文本」框（可继续微调）
 * → 「保存」走内联编辑同一 `updateTrainingDatasetItem`；取消/遮罩不产生任何请求。
 * 识别失败仅提示，不破坏原有文本。
 */
export function ReRecognizeDialog({
  datasetId,
  item,
  draft,
  onClose,
  onSaved,
}: ReRecognizeDialogProps) {
  const [models, setModels] = useState<TrainingModel[]>([])
  const [modelId, setModelId] = useState('')
  // 有内联草稿时以草稿文本为起点，保证「仅保存」也能保留用户的内联文本修改。
  const [text, setText] = useState(draft?.text ?? item.text)
  const [error, setError] = useState('')
  const [recognizing, setRecognizing] = useState(false)
  const [saving, setSaving] = useState(false)

  const busy = recognizing || saving

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

  const handleRecognize = async () => {
    if (!modelId || busy) return
    setRecognizing(true)
    setError('')
    try {
      const result = await recognizeTrainingDatasetItem(datasetId, item.id, modelId)
      setText(result.text)
    } catch (err) {
      // 识别失败保留原有文本，仅提示错误。
      setError(apiErrorMessage(err, '识别失败'))
    } finally {
      setRecognizing(false)
    }
  }

  const handleSave = async () => {
    if (busy) return

    // 有内联草稿时保存行为与内联保存一致：一并提交 audioUri/startMs/endMs，避免未保存的修改被静默丢弃。
    let payload: {
      text: string
      audioUri?: string
      startMs?: number
      endMs?: number | null
    } = { text }
    if (draft) {
      const startMs = parsePosition(draft.start)
      const hasEnd = draft.end.trim() !== ''
      const endMs = hasEnd ? parsePosition(draft.end) : null
      if (startMs === null || (hasEnd && endMs === null) || (endMs !== null && endMs <= startMs)) {
        setError(TIME_INVALID)
        return
      }
      payload = { audioUri: draft.audioUri, text, startMs, endMs }
    }

    setSaving(true)
    setError('')
    try {
      const updated = await updateTrainingDatasetItem(datasetId, item.id, payload)
      onSaved(updated)
    } catch (err) {
      setError(apiErrorMessage(err, '保存失败'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      title="重新识别"
      open
      onCancel={onClose}
      maskClosable={false}
      destroyOnHidden
      footer={[
        <Button key="recognize" type="primary" loading={recognizing} disabled={!modelId || busy} onClick={handleRecognize}>
          识别
        </Button>,
        <Button key="save" loading={saving} disabled={busy} onClick={handleSave}>
          保存
        </Button>,
        <Button key="cancel" disabled={busy} onClick={onClose}>
          取消
        </Button>,
      ]}
    >
      <Form layout="vertical">
        <Form.Item label="识别模型">
          <Select
            aria-label="识别模型"
            value={modelId || undefined}
            placeholder="请选择识别模型"
            onChange={setModelId}
            options={models.map((model) => ({ value: model.id as string, label: model.slug as string }))}
          />
        </Form.Item>
        <Form.Item label="文本">
          <Input.TextArea
            aria-label="文本"
            rows={5}
            value={text}
            onChange={(e) => setText(e.target.value)}
          />
        </Form.Item>
      </Form>

      {models.length === 0 && <Alert type="info" showIcon message="请先在模型设置中配置" />}
      {error && <Alert type="error" showIcon message={error} />}
    </Modal>
  )
}
