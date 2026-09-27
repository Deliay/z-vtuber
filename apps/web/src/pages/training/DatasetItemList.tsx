import {
  Fragment,
  useCallback,
  useEffect,
  useMemo,
  useState,
  type CSSProperties,
  type MouseEvent,
} from 'react'
import {
  apiErrorMessage,
  batchDeleteTrainingDatasetItems,
  listTrainingDatasetItems,
  updateTrainingDatasetItem,
} from '../../api/client'
import { formatPosition, parsePosition } from '../../utils/format'
import { DatasetItemWaveform } from './DatasetItemWaveform'
import type { WaveformLoader } from './waveformLoader'
import type { components } from '../../api/schema-training'

type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']

export interface DatasetItemListProps {
  datasetId: string
  /** 数据集条目变化（如导入完成）后的刷新令牌。 */
  reloadToken?: number
  /** 可注入的波形加载器（透传给 `DatasetItemWaveform`）。 */
  waveformLoader?: WaveformLoader
}

type GroupMode = 'none' | 'source' | 'audioType'

interface EditDraft {
  audioUri: string
  text: string
  start: string
  end: string
}

const TIME_INVALID = '时间输入无效'

/**
 * 次要信息列（音频来源/音频地址/音频标识）样式：低价值长文本限宽 + 省略号截断，
 * 完整值经单元格 `title` 悬浮展示（配合表头 `<colgroup>` 固定列宽，避免撑宽表格挤压「文本」列）。
 * 参考 `SegmentAudioPlayer.tsx` 的 ellipsis 先例。
 */
const TRUNCATED_CELL_STYLE: CSSProperties = {
  maxWidth: '12rem',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
}

/** 「文本」列样式：占满剩余宽度、正常换行（长串按需断行）展示全文，不设 max-width。 */
const TEXT_CELL_STYLE: CSSProperties = {
  whiteSpace: 'normal',
  wordBreak: 'break-word',
}

/** 编辑态文本框：占满单元格且不撑破列宽约束。 */
const EDIT_CONTROL_STYLE: CSSProperties = {
  width: '100%',
  boxSizing: 'border-box',
}

/** 服务端错误 → 可读提示（错误码映射见 `apiErrorMessage`）。 */
function errorMessage(error: unknown): string {
  return apiErrorMessage(error)
}

/**
 * 音频数据列表：七字段展示（音频标识不可编辑）、四项字段编辑（`m:ss`/`m:ss.S` ↔ 毫秒、
 * `endMs` 可置空、非法时间拦截）、多选批量删除（二次确认）、
 * 不分组/按来源/按类型分组浏览与分组头全选（联动批量删除）。
 * 数据一次拉全后在客户端聚合分组。
 */
export function DatasetItemList({
  datasetId,
  reloadToken = 0,
  waveformLoader,
}: DatasetItemListProps) {
  const [items, setItems] = useState<TrainingDatasetItem[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [groupMode, setGroupMode] = useState<GroupMode>('none')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [draft, setDraft] = useState<EditDraft>({ audioUri: '', text: '', start: '', end: '' })
  const [editError, setEditError] = useState('')
  const [previewId, setPreviewId] = useState<string | null>(null)
  const [error, setError] = useState('')

  const refresh = useCallback(async () => {
    try {
      const list = await listTrainingDatasetItems(datasetId)
      setItems(list)
      setSelected((prev) => prev.filter((id) => list.some((item) => item.id === id)))
      setError('')
    } catch (err) {
      setError(errorMessage(err))
    }
  }, [datasetId])

  useEffect(() => {
    void refresh()
  }, [refresh, reloadToken])

  const groups = useMemo(() => {
    if (groupMode === 'none') return null
    const buckets = new Map<string, TrainingDatasetItem[]>()
    for (const item of items) {
      const key = groupMode === 'source' ? item.source : item.audioType
      const bucket = buckets.get(key) ?? []
      bucket.push(item)
      buckets.set(key, bucket)
    }
    return Array.from(buckets.entries())
  }, [items, groupMode])

  const allChecked =
    items.length > 0 && items.every((item) => selected.includes(item.id))

  const stopPropagation = (event: MouseEvent) => {
    event.stopPropagation()
  }

  const toggleItem = (id: string) => {
    setSelected((prev) =>
      prev.includes(id) ? prev.filter((value) => value !== id) : [...prev, id],
    )
  }

  const toggleGroup = (groupIds: string[]) => {
    setSelected((prev) => {
      const allIn = groupIds.every((id) => prev.includes(id))
      if (allIn) {
        return prev.filter((id) => !groupIds.includes(id))
      }
      const next = [...prev]
      for (const id of groupIds) {
        if (!next.includes(id)) next.push(id)
      }
      return next
    })
  }

  const toggleAll = () => {
    setSelected(allChecked ? [] : items.map((item) => item.id))
  }

  const startEdit = (item: TrainingDatasetItem) => {
    setEditingId(item.id)
    setEditError('')
    setDraft({
      audioUri: item.audioUri,
      text: item.text,
      start: formatPosition(item.startMs),
      end:
        item.endMs === null || item.endMs === undefined
          ? ''
          : formatPosition(item.endMs),
    })
  }

  const cancelEdit = () => {
    setEditingId(null)
    setEditError('')
  }

  const saveEdit = async (item: TrainingDatasetItem) => {
    const startMs = parsePosition(draft.start)
    const hasEnd = draft.end.trim() !== ''
    const endMs = hasEnd ? parsePosition(draft.end) : null

    if (startMs === null || (hasEnd && endMs === null) || (endMs !== null && endMs <= startMs)) {
      setEditError(TIME_INVALID)
      return
    }

    try {
      const updated = await updateTrainingDatasetItem(datasetId, item.id, {
        audioUri: draft.audioUri,
        text: draft.text,
        startMs,
        endMs,
      })
      setItems((prev) => prev.map((value) => (value.id === item.id ? updated : value)))
      cancelEdit()
    } catch (err) {
      setEditError(errorMessage(err))
    }
  }

  const handleBatchDelete = async () => {
    if (selected.length === 0) return
    const confirmed = window.confirm(
      `确认删除选中的 ${selected.length} 条音频数据？删除后不可恢复。`,
    )
    if (!confirmed) return

    try {
      await batchDeleteTrainingDatasetItems(datasetId, selected)
      if (previewId && selected.includes(previewId)) setPreviewId(null)
      setSelected([])
      await refresh()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  const renderRow = (item: TrainingDatasetItem) => {
    const editing = editingId === item.id
    return (
      <tr key={item.id} onClick={() => setPreviewId(item.id)}>
        <td onClick={stopPropagation}>
          <input
            type="checkbox"
            aria-label={`选择 ${item.audioKey}`}
            checked={selected.includes(item.id)}
            onChange={() => toggleItem(item.id)}
          />
        </td>
        <td>{item.audioType}</td>
        <td style={TRUNCATED_CELL_STYLE} title={item.source}>
          {item.source}
        </td>
        <td onClick={stopPropagation} style={TRUNCATED_CELL_STYLE} title={item.audioUri || undefined}>
          {editing ? (
            <input
              aria-label="音频地址"
              style={EDIT_CONTROL_STYLE}
              value={draft.audioUri}
              onChange={(event) => setDraft({ ...draft, audioUri: event.target.value })}
            />
          ) : (
            item.audioUri || '无'
          )}
        </td>
        <td style={TRUNCATED_CELL_STYLE} title={item.audioKey}>
          {item.audioKey}
        </td>
        <td onClick={stopPropagation}>
          {editing ? (
            <input
              aria-label="开始时间"
              style={EDIT_CONTROL_STYLE}
              value={draft.start}
              onChange={(event) => setDraft({ ...draft, start: event.target.value })}
            />
          ) : (
            formatPosition(item.startMs)
          )}
        </td>
        <td onClick={stopPropagation}>
          {editing ? (
            <input
              aria-label="结束时间"
              style={EDIT_CONTROL_STYLE}
              value={draft.end}
              onChange={(event) => setDraft({ ...draft, end: event.target.value })}
            />
          ) : (
            formatPosition(item.endMs)
          )}
        </td>
        <td onClick={stopPropagation} style={TEXT_CELL_STYLE}>
          {editing ? (
            <textarea
              aria-label="文本"
              rows={3}
              style={{ ...EDIT_CONTROL_STYLE, resize: 'vertical' }}
              value={draft.text}
              onChange={(event) => setDraft({ ...draft, text: event.target.value })}
            />
          ) : (
            item.text
          )}
        </td>
        <td onClick={stopPropagation}>
          {editing ? (
            <>
              <button onClick={() => void saveEdit(item)}>保存</button>
              <button onClick={cancelEdit}>取消</button>
            </>
          ) : (
            <button aria-label={`编辑 ${item.audioKey}`} onClick={() => startEdit(item)}>
              编辑
            </button>
          )}
        </td>
      </tr>
    )
  }

  const previewItem = items.find((item) => item.id === previewId)

  return (
    <div className="dataset-item-list">
      {error && <div role="alert">{error}</div>}

      {items.length === 0 ? (
        <div>暂无音频数据，可从 Transcript 导入</div>
      ) : (
        <>
          <div className="dataset-item-toolbar">
            <select
              aria-label="分组方式"
              value={groupMode}
              onChange={(event) => setGroupMode(event.target.value as GroupMode)}
            >
              <option value="none">不分组</option>
              <option value="source">按来源</option>
              <option value="audioType">按类型</option>
            </select>
            <button onClick={() => void handleBatchDelete()} disabled={selected.length === 0}>
              批量删除
            </button>
            {editError && <div role="alert">{editError}</div>}
          </div>

          <table
            style={{ width: '100%', borderCollapse: 'collapse', tableLayout: 'fixed' }}
          >
            {/* 固定短列/次要列宽度，让「文本」列（不设宽度）占满剩余宽度 */}
            <colgroup>
              <col style={{ width: '2.5rem' }} />
              <col style={{ width: '6rem' }} />
              <col style={{ width: '9rem' }} />
              <col style={{ width: '12rem' }} />
              <col style={{ width: '12rem' }} />
              <col style={{ width: '5rem' }} />
              <col style={{ width: '5rem' }} />
              <col />
              <col style={{ width: '6rem' }} />
            </colgroup>
            <thead>
              <tr>
                <th>
                  <input
                    type="checkbox"
                    aria-label="全选"
                    checked={allChecked}
                    onChange={toggleAll}
                  />
                </th>
                <th>音频类型</th>
                <th style={TRUNCATED_CELL_STYLE}>音频来源</th>
                <th style={TRUNCATED_CELL_STYLE}>音频地址</th>
                <th style={TRUNCATED_CELL_STYLE}>音频标识</th>
                <th>开始时间</th>
                <th>结束时间</th>
                <th style={TEXT_CELL_STYLE}>文本</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {groups
                ? groups.map(([key, groupItems]) => {
                    const groupIds = groupItems.map((item) => item.id)
                    const label = groupMode === 'source' ? '来源' : '类型'
                    return (
                      <Fragment key={`${groupMode}-${key}`}>
                        <tr>
                          <th colSpan={9}>
                            <label>
                              <input
                                type="checkbox"
                                aria-label={`全选分组 ${key}`}
                                checked={groupIds.every((id) => selected.includes(id))}
                                onChange={() => toggleGroup(groupIds)}
                              />
                              {`${label}: ${key}（${groupItems.length} 条）`}
                            </label>
                          </th>
                        </tr>
                        {groupItems.map(renderRow)}
                      </Fragment>
                    )
                  })
                : items.map(renderRow)}
            </tbody>
          </table>
        </>
      )}

      {previewItem && (
        <DatasetItemWaveform item={previewItem} loader={waveformLoader} />
      )}
    </div>
  )
}
