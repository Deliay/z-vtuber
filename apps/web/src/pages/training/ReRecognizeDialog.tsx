import { useEffect, useState, type CSSProperties } from 'react'
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
            {recognizing ? '识别中...' : '识别'}
          </button>
          <button onClick={() => void handleSave()} disabled={busy}>
            {saving ? '保存中...' : '保存'}
          </button>
          <button onClick={onClose} disabled={busy}>
            取消
          </button>
        </div>
      </div>
    </div>
  )
}
