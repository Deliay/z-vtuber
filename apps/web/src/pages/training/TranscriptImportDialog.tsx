import { useEffect, useState } from 'react'
import { Alert, Button, Form, Modal, Select, Typography } from 'antd'
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
    <Modal
      title="从 Transcript 导入"
      open
      onCancel={onClose}
      mask={{ closable: !busy }}
      destroyOnHidden
      footer={[
        <Button key="import" type="primary" loading={busy} disabled={!taskId} onClick={handleImport}>
          确认导入
        </Button>,
        <Button key="close" disabled={busy} onClick={onClose}>
          {result ? '完成' : '取消'}
        </Button>,
      ]}
    >
      <Form layout="vertical">
        <Form.Item label="识别任务">
          <Select
            aria-label="识别任务"
            value={taskId || undefined}
            placeholder="请选择识别任务"
            onChange={setTaskId}
            options={tasks.map((task) => ({ value: task.id as string, label: taskLabel(task) }))}
          />
        </Form.Item>
      </Form>

      {tasks.length === 0 && !error && (
        <Typography.Text type="secondary">暂无已识别完成的任务</Typography.Text>
      )}
      {error && <Alert type="error" showIcon title={error} />}
      {result && (
        <Alert
          type="success"
          showIcon
          title={`已导入 ${result.imported} 条，跳过 ${result.skipped} 条`}
        />
      )}
    </Modal>
  )
}
