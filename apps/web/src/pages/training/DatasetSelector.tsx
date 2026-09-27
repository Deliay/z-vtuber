import { useEffect, useRef, useState } from 'react'
import {
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

/** 服务端错误体 → 页面提示（重名必须明确提示且不生效）。 */
function errorMessage(error: unknown): string {
  const serverMessage = (error as { serverMessage?: string })?.serverMessage ?? ''
  if (serverMessage.includes('dataset name exists')) {
    return '数据集名字已存在'
  }
  if (serverMessage.includes('name is required')) {
    return '数据集名字不能为空'
  }
  if (serverMessage.includes('name too long')) {
    return '数据集名字过长（最多 128 字符）'
  }
  return serverMessage || '操作失败'
}

/**
 * 音频数据集下拉 + 新建/重命名/删除（二次确认）。
 * 首次加载自动选中第一个数据集；删除后选中项回退到剩余第一个（无剩余则清空）。
 */
export function DatasetSelector({ selectedId, onSelect }: DatasetSelectorProps) {
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
      const created = await createTrainingDataset({
        name: name.trim(),
        type: DATASET_TYPE,
      })
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

  const handleDelete = async () => {
    const target = datasets.find((dataset) => dataset.id === selectedId)
    if (!target) return

    const confirmed = window.confirm(
      `确认删除数据集「${target.name}」？该数据集及其全部音频数据将一并清除，不可恢复。`,
    )
    if (!confirmed) return

    try {
      await deleteTrainingDataset(target.id)
      const list = await reload()
      setError('')
      onSelect(list.length > 0 ? list[0].id : '')
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <div className="dataset-selector">
      <label htmlFor="dataset-select">数据集</label>
      <select
        id="dataset-select"
        value={selectedId}
        onChange={(event) => onSelect(event.target.value)}
      >
        <option value="">（未选择）</option>
        {datasets.map((dataset) => (
          <option key={dataset.id} value={dataset.id}>
            {dataset.name}
          </option>
        ))}
      </select>

      <button onClick={() => startEdit('create')}>新建数据集</button>
      <button onClick={() => startEdit('rename')} disabled={!selectedId}>
        重命名数据集
      </button>
      <button onClick={handleDelete} disabled={!selectedId}>
        删除数据集
      </button>

      {mode !== 'idle' && (
        <span className="dataset-name-editor">
          <label htmlFor="dataset-name">数据集名字</label>
          <input
            id="dataset-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <button onClick={mode === 'create' ? handleCreate : handleRename}>
            确认
          </button>
          <button onClick={cancelEdit}>取消</button>
        </span>
      )}

      {error && (
        <div role="alert" className="dataset-error">
          {error}
        </div>
      )}
    </div>
  )
}
