import { useEffect, useState } from 'react'
import {
  apiErrorMessage,
  importTrainingDatasetItems,
  listTranscriptTasks,
} from '../../api/client'
import type { components } from '../../api/schema-recording-transcript'

type TranscriptTask = components['schemas']['TranscriptTask']

export interface TranscriptImportResult {
  imported: number
  skipped: number
}

export interface TranscriptImportDialogProps {
  datasetId: string
  onClose: () => void
  /** 导入成功回调（用于刷新列表）。 */
  onImported?: (result: TranscriptImportResult) => void
}

/** 服务端错误 → 可读提示（错误码映射见 `apiErrorMessage`）。 */
function errorMessage(error: unknown): string {
  return apiErrorMessage(error, '导入失败')
}

function taskLabel(task: TranscriptTask): string {
  const title = task.title ?? task.roomId ?? task.id ?? ''
  return `${title}（${task.id ?? ''}）`
}

/**
 * 从识别结果导入：只列出 `status=DONE` 的任务，确认后导入并反馈导入/跳过条数；
 * 取消不发请求；400/502 等错误以服务端消息提示（非阻断）。
 */
export function TranscriptImportDialog({
  datasetId,
  onClose,
  onImported,
}: TranscriptImportDialogProps) {
  const [tasks, setTasks] = useState<TranscriptTask[]>([])
  const [taskId, setTaskId] = useState('')
  const [error, setError] = useState('')
  const [result, setResult] = useState<TranscriptImportResult | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let active = true
    listTranscriptTasks({ status: 'DONE' })
      .then((list) => {
        if (!active) return
        // 双保险：即使上游忽略过滤，也只保留识别完成的任务。
        setTasks(list.filter((task) => task.status === 'DONE' && task.id))
      })
      .catch((err) => {
        if (active) setError(errorMessage(err))
      })
    return () => {
      active = false
    }
  }, [])

  const handleImport = async () => {
    if (!taskId || busy) return
    setBusy(true)
    setError('')
    try {
      const imported = await importTrainingDatasetItems(datasetId, { taskId })
      setResult(imported)
      onImported?.(imported)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div
      className="transcript-import-overlay"
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
      // 点击遮罩关闭（面板内的点击已 stopPropagation，不会误触）
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-label="从 Transcript 导入"
        aria-modal="true"
        className="transcript-import-dialog"
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
        <h3>从 Transcript 导入</h3>

        <label htmlFor="import-task">识别任务</label>
        <select
          id="import-task"
          value={taskId}
          onChange={(event) => setTaskId(event.target.value)}
        >
          <option value="">请选择识别任务</option>
          {tasks.map((task) => (
            <option key={task.id} value={task.id}>
              {taskLabel(task)}
            </option>
          ))}
        </select>

        {tasks.length === 0 && !error && (
          <div style={{ color: '#94a3b8' }}>暂无已识别完成的任务</div>
        )}
        {error && <div role="alert">{error}</div>}
        {result && (
          <div role="status">
            已导入 {result.imported} 条，跳过 {result.skipped} 条
          </div>
        )}

        <button onClick={handleImport} disabled={!taskId || busy}>
          确认导入
        </button>
        <button onClick={onClose}>{result ? '完成' : '取消'}</button>
      </div>
    </div>
  )
}
