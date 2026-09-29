import { useEffect, useState, type CSSProperties } from 'react'
import {
  apiErrorMessage,
  listTrainingModels,
  recognizeTrainingDatasetItem,
  updateTrainingDatasetItem,
} from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingModel = components['schemas']['TrainingModel']
type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']

export interface ReRecognizeDialogItem {
  id: string
  text: string
}

export interface ReRecognizeDialogProps {
  datasetId: string
  item: ReRecognizeDialogItem
  onClose: () => void
  /** 保存成功后回调（携带更新后的条目）。 */
  onSaved: (updated: TrainingDatasetItem) => void
}

const inputStyle: CSSProperties = {
  width: '100%',
  boxSizing: 'border-box',
  padding: '0.5rem',
  backgroundColor: '#334155',
  color: '#e2e8f0',
  border: '1px solid #475569',
  borderRadius: '0.25rem',
}

/**
 * 单条「重新识别」对话框：选择已启用模型 → 识别回填「文本」框（可继续微调）
 * → 「保存」走内联编辑同一 `updateTrainingDatasetItem`；取消/遮罩不产生任何请求。
 * 识别失败仅提示，不破坏原有文本。
 */
export function ReRecognizeDialog({
  datasetId,
  item,
  onClose,
  onSaved,
}: ReRecognizeDialogProps) {
  const [models, setModels] = useState<TrainingModel[]>([])
  const [modelId, setModelId] = useState('')
  const [text, setText] = useState(item.text)
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
    setSaving(true)
    setError('')
    try {
      const updated = await updateTrainingDatasetItem(datasetId, item.id, { text })
      onSaved(updated)
    } catch (err) {
      setError(apiErrorMessage(err, '保存失败'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div
      className="re-recognize-overlay"
      style={{
        position: 'fixed',
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        backgroundColor: 'rgba(0, 0, 0, 0.5)',
        display: 'flex',
        justifyContent: 'center',
        alignItems: 'center',
        zIndex: 1000,
      }}
      onClick={() => {
        if (!busy) onClose()
      }}
    >
      <div
        role="dialog"
        aria-label="重新识别"
        aria-modal="true"
        className="re-recognize-dialog"
        style={{
          backgroundColor: '#1e293b',
          color: '#e2e8f0',
          padding: '1.5rem',
          borderRadius: '0.5rem',
          minWidth: '400px',
          maxWidth: '90vw',
          maxHeight: '90vh',
          overflow: 'auto',
        }}
        onClick={(event) => event.stopPropagation()}
      >
        <h3>重新识别</h3>

        <label htmlFor="re-recognize-model">识别模型</label>
        <select
          id="re-recognize-model"
          aria-label="识别模型"
          style={inputStyle}
          value={modelId}
          onChange={(event) => setModelId(event.target.value)}
        >
          <option value="">请选择识别模型</option>
          {models.map((model) => (
            <option key={model.id} value={model.id}>
              {model.slug}
            </option>
          ))}
        </select>

        {models.length === 0 && (
          <div style={{ color: '#94a3b8' }}>请先在模型设置中配置</div>
        )}

        <label htmlFor="re-recognize-text">文本</label>
        <textarea
          id="re-recognize-text"
          aria-label="文本"
          rows={5}
          style={{ ...inputStyle, resize: 'vertical' }}
          value={text}
          onChange={(event) => setText(event.target.value)}
        />

        {error && <div role="alert">{error}</div>}

        <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem' }}>
          <button onClick={() => void handleRecognize()} disabled={!modelId || busy}>
            识别
          </button>
          <button onClick={() => void handleSave()} disabled={busy}>
            保存
          </button>
          <button onClick={onClose} disabled={busy}>
            取消
          </button>
        </div>
      </div>
    </div>
  )
}
