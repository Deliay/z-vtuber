import { useEffect, useState, type CSSProperties } from 'react'
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
    <div
      className="emotion-submit-overlay"
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
        aria-label="情绪识别"
        aria-modal="true"
        className="emotion-submit-dialog"
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
        <h3>情绪识别</h3>
        <div>已选择 {itemIds.length} 条数据</div>

        <label htmlFor="emotion-model">识别模型</label>
        <select
          id="emotion-model"
          aria-label="情绪识别模型"
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

        {error && <div role="alert">{error}</div>}
        {result && (
          <div role="status">
            已提交 {result.created} 条，跳过 {result.skipped} 条
          </div>
        )}

        <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem' }}>
          <button onClick={() => void handleSubmit()} disabled={!modelId || busy}>
            提交
          </button>
          <button onClick={onClose} disabled={busy}>
            取消
          </button>
        </div>
      </div>
    </div>
  )
}
