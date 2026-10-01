import { useEffect, useRef, useState } from 'react'
import { Alert, App as AntdApp, Button, Input, Select, Space, Typography } from 'antd'
import {
  apiErrorMessage,
  createTrainingDataset,
  deleteTrainingDataset,
  listTrainingDatasets,
  renameTrainingDataset,
} from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingDataset = components['schemas']['TrainingDataset']

export interface DatasetSelectorProps {
  selectedId: string
  onSelect: (datasetId: string) => void
}

/** 本期唯一合法的数据集类型。 */
const DATASET_TYPE = 'audio'

type EditMode = 'idle' | 'create' | 'rename'

/** 服务端错误 → 可读提示（错误码映射见 `apiErrorMessage`）。 */
function errorMessage(error: unknown): string {
  return apiErrorMessage(error)
}

/**
 * 音频数据集下拉 + 新建/重命名/删除（二次确认）。
 * 首次加载自动选中第一个数据集；删除后选中项回退到剩余第一个（无剩余则清空）。
 */
export function DatasetSelector({ selectedId, onSelect }: DatasetSelectorProps) {
  const { modal } = AntdApp.useApp()
  const [datasets, setDatasets] = useState<TrainingDataset[]>([])
  const [mode, setMode] = useState<EditMode>('idle')
  const [name, setName] = useState('')
  const [error, setError] = useState('')

  // 仅首次挂载需要读取一次入参：用 ref 捕获，保持「挂载后自动选中首个」只发生一次（不随 props 变化重跑）。
  const initialProps = useRef({ selectedId, onSelect })
  initialProps.current = { selectedId, onSelect }

  useEffect(() => {
    let active = true
    listTrainingDatasets(DATASET_TYPE)
      .then((list) => {
        if (!active) return
        setDatasets(list)
        const { selectedId: initialSelectedId, onSelect: select } = initialProps.current
        if (!initialSelectedId && list.length > 0) {
          select(list[0].id)
        }
      })
      .catch((err) => {
        if (active) setError(errorMessage(err))
      })
    return () => {
      active = false
    }
    // 仅首次挂载拉取；后续增删改走 reload。
  }, [])

  const reload = async (): Promise<TrainingDataset[]> => {
    const list = await listTrainingDatasets(DATASET_TYPE)
    setDatasets(list)
    return list
  }

  const startEdit = (nextMode: Exclude<EditMode, 'idle'>) => {
    setError('')
    setName('')
    setMode(nextMode)
  }

  const cancelEdit = () => {
    setMode('idle')
    setName('')
    setError('')
  }

  const handleCreate = async () => {
    try {
      const created = await createTrainingDataset({ name: name.trim(), type: DATASET_TYPE })
      await reload()
      cancelEdit()
      onSelect(created.id)
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  const handleRename = async () => {
    try {
      await renameTrainingDataset(selectedId, { name: name.trim() })
      await reload()
      cancelEdit()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  const handleDelete = () => {
    const target = datasets.find((dataset) => dataset.id === selectedId)
    if (!target) return

    modal.confirm({
      title: `确认删除数据集「${target.name}」？`,
      content: '该数据集及其全部音频数据将一并清除，不可恢复。',
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          await deleteTrainingDataset(target.id)
          const list = await reload()
          setError('')
          onSelect(list.length > 0 ? list[0].id : '')
        } catch (err) {
          setError(errorMessage(err))
        }
      },
    })
  }

  return (
    <div className="dataset-selector">
      <Space wrap align="center">
        <Typography.Text>数据集</Typography.Text>
        <Select
          id="dataset-select"
          aria-label="数据集"
          value={selectedId || undefined}
          placeholder="（未选择）"
          style={{ width: 220 }}
          onChange={(value) => onSelect(value)}
          options={[
            { value: '', label: '（未选择）' },
            ...datasets.map((dataset) => ({ value: dataset.id, label: dataset.name })),
          ]}
        />
        <Button onClick={() => startEdit('create')}>新建数据集</Button>
        <Button onClick={() => startEdit('rename')} disabled={!selectedId}>
          重命名数据集
        </Button>
        <Button danger onClick={handleDelete} disabled={!selectedId}>
          删除数据集
        </Button>
      </Space>

      {mode !== 'idle' && (
        <Space wrap align="center" style={{ marginTop: 12 }}>
          <Typography.Text>数据集名字</Typography.Text>
          <Input
            id="dataset-name"
            aria-label="数据集名字"
            value={name}
            style={{ width: 200 }}
            onChange={(event) => setName(event.target.value)}
          />
          <Button type="primary" onClick={mode === 'create' ? handleCreate : handleRename}>
            确认
          </Button>
          <Button onClick={cancelEdit}>取消</Button>
        </Space>
      )}

      {error && (
        <Alert
          type="error"
          showIcon
          message={error}
          style={{ marginTop: 12 }}
          role="alert"
        />
      )}
    </div>
  )
}
