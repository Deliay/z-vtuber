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
import { ReRecognizeDialog } from './ReRecognizeDialog'
import { EmotionSubmitDialog } from './EmotionSubmitDialog'
import type { WaveformLoader } from './waveformLoader'
import type { components } from '../../api/schema-training'

type TrainingDatasetItem = components['schemas']['TrainingDatasetItem']
type EmotionResult = components['schemas']['EmotionResult']

export interface DatasetItemListProps {
  datasetId: string
  /** 数据集条目变化（如导入完成）后的刷新令牌。 */
  reloadToken?: number
  /** 可注入的波形加载器（透传给 `DatasetItemWaveform`）。 */
  waveformLoader?: WaveformLoader
  /** 列表数据变化（如情绪任务提交）后的外部回调（用于刷新任务队列等）。 */
  onChanged?: () => void
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

/** 情绪数值列：限窄且不换行。 */
const EMOTION_VALUE_CELL_STYLE: CSSProperties = {
  whiteSpace: 'nowrap',
  maxWidth: '5rem',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
}

/** 编辑态文本框：占满单元格且不撑破列宽约束。 */
const EDIT_CONTROL_STYLE: CSSProperties = {
  width: '100%',
  boxSizing: 'border-box',
}

/** 「音频信息」六个可选列（默认隐藏）。 */
const AUDIO_COLUMNS: { key: keyof TrainingDatasetItem; label: string; width: string; truncate?: boolean }[] = [
  { key: 'audioType', label: '音频类型', width: '6rem' },
  { key: 'source', label: '音频来源', width: '9rem', truncate: true },
  { key: 'audioUri', label: '音频地址', width: '12rem', truncate: true },
  { key: 'audioKey', label: '音频标识', width: '12rem', truncate: true },
  { key: 'startMs', label: '开始时间', width: '5rem' },
  { key: 'endMs', label: '结束时间', width: '5rem' },
]

/** 「情绪信息」十个可选列（默认展示）。 */
const EMOTION_COLUMNS: { key: keyof EmotionResult; label: string; width: string; truncate?: boolean }[] = [
  { key: 'emotionControlWeight', label: '情绪控制权重', width: '6rem' },
  { key: 'emotionDescription', label: '情绪描述', width: '12rem', truncate: true },
  { key: 'happy', label: '开心', width: '4rem' },
  { key: 'angry', label: '愤怒', width: '4rem' },
  { key: 'sad', label: '悲伤', width: '4rem' },
  { key: 'afraid', label: '恐惧', width: '4rem' },
  { key: 'disgusted', label: '厌恶', width: '4rem' },
  { key: 'melancholic', label: '忧郁', width: '4rem' },
  { key: 'surprised', label: '惊讶', width: '4rem' },
  { key: 'calm', label: '平静', width: '4rem' },
]

/** 服务端错误 → 可读提示（错误码映射见 `apiErrorMessage`）。 */
function errorMessage(error: unknown): string {
  return apiErrorMessage(error)
}

/** 单个情绪字段展示值：缺失/空值一律展示 `-`（悬停完整文本）。 */
function emotionCellValue(
  emotion: EmotionResult | null | undefined,
  key: keyof EmotionResult,
): { text: string; title?: string } {
  if (!emotion) return { text: '-' }
  const value = emotion[key]
  if (value === null || value === undefined || value === '') return { text: '-' }
  const text = String(value)
  return { text, title: text }
}

/**
 * 音频数据列表：七字段展示（音频标识不可编辑）、四项字段编辑（`m:ss`/`m:ss.S` ↔ 毫秒、
 * `endMs` 可置空、非法时间拦截）、多选批量删除（二次确认）、
 * 不分组/按来源/按类型分组浏览与分组头全选（联动批量删除）。
 * 工具栏「展示类型」控制音频信息（默认隐藏）/情绪信息（默认展示）列显隐；
 * 编辑态可打开「重新识别」、多选后可提交「情绪识别」。数据一次拉全后在客户端聚合分组。
 */
export function DatasetItemList({
  datasetId,
  reloadToken = 0,
  waveformLoader,
  onChanged,
}: DatasetItemListProps) {
  const [items, setItems] = useState<TrainingDatasetItem[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [groupMode, setGroupMode] = useState<GroupMode>('none')
  const [showAudioInfo, setShowAudioInfo] = useState(false)
  const [showEmotion, setShowEmotion] = useState(true)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [draft, setDraft] = useState<EditDraft>({ audioUri: '', text: '', start: '', end: '' })
  const [editError, setEditError] = useState('')
  const [previewId, setPreviewId] = useState<string | null>(null)
  const [reRecognizeId, setReRecognizeId] = useState<string | null>(null)
  const [emotionDialogOpen, setEmotionDialogOpen] = useState(false)
  const [error, setError] = useState('')
  /** 内联保存/批量删除进行中标志（防重复提交）。 */
  const [savingEdit, setSavingEdit] = useState(false)
  const [deleting, setDeleting] = useState(false)

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

  // 可见列数：勾选 + 音频信息六列（可选）+ 文本 + 情绪信息十列（可选）+ 操作。
  const visibleColumnCount =
    1 + (showAudioInfo ? AUDIO_COLUMNS.length : 0) + 1 + (showEmotion ? EMOTION_COLUMNS.length : 0) + 1

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
    if (savingEdit) return
    const startMs = parsePosition(draft.start)
    const hasEnd = draft.end.trim() !== ''
    const endMs = hasEnd ? parsePosition(draft.end) : null

    if (startMs === null || (hasEnd && endMs === null) || (endMs !== null && endMs <= startMs)) {
      setEditError(TIME_INVALID)
      return
    }

    setSavingEdit(true)
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
    } finally {
      setSavingEdit(false)
    }
  }

  const handleBatchDelete = async () => {
    if (selected.length === 0 || deleting) return
    const confirmed = window.confirm(
      `确认删除选中的 ${selected.length} 条音频数据？删除后不可恢复。`,
    )
    if (!confirmed) return

    setDeleting(true)
    try {
      await batchDeleteTrainingDatasetItems(datasetId, selected)
      if (previewId && selected.includes(previewId)) setPreviewId(null)
      setSelected([])
      await refresh()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setDeleting(false)
    }
  }

  /**
   * 展开行：承载被点击条目的波形试听，紧贴在条目行正下方（随表格滚动），
   * `colSpan` 覆盖当前可见列、不破坏 `colgroup`/`table-layout:fixed` 列宽约束。
   * 点击行内编辑控件的 `stopPropagation` 保持不变（点编辑不误触发展开/收起冲突）。
   */
  const renderWaveformExpandRow = (item: TrainingDatasetItem) => (
    <tr key={`waveform-${item.id}`} className="dataset-item-waveform-row">
      <td colSpan={visibleColumnCount} style={{ padding: 0 }}>
        <DatasetItemWaveform item={item} loader={waveformLoader} />
      </td>
    </tr>
  )

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
        {showAudioInfo && (
          <>
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
          </>
        )}
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
        {showEmotion &&
          EMOTION_COLUMNS.map((column) => {
            const cell = emotionCellValue(item.emotion, column.key)
            const style = column.truncate ? TRUNCATED_CELL_STYLE : EMOTION_VALUE_CELL_STYLE
            return (
              <td key={column.key} style={style} title={cell.title}>
                {cell.text}
              </td>
            )
          })}
        <td onClick={stopPropagation}>
          {editing ? (
            <>
              <button onClick={() => void saveEdit(item)} disabled={savingEdit}>
                保存
              </button>
              <button onClick={cancelEdit}>取消</button>
              <button onClick={() => setReRecognizeId(item.id)}>重新识别</button>
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

  /** 条目行 + （命中时）紧贴其下的波形展开行；同一时刻仅 `previewId` 对应一行展开。 */
  const renderRowWithWaveform = (item: TrainingDatasetItem) => (
    <Fragment key={item.id}>
      {renderRow(item)}
      {previewId === item.id && renderWaveformExpandRow(item)}
    </Fragment>
  )

  const reRecognizeItem = reRecognizeId
    ? items.find((item) => item.id === reRecognizeId)
    : undefined

  return (
    <div className="dataset-item-list">
      {error && <div role="alert">{error}</div>}

      {items.length === 0 ? (
        <div>暂无音频数据，可从 Transcript 导入</div>
      ) : (
        <>
          <div className="dataset-item-toolbar">
            <span>展示类型</span>
            <label>
              <input
                type="checkbox"
                aria-label="音频信息"
                checked={showAudioInfo}
                onChange={(event) => setShowAudioInfo(event.target.checked)}
              />
              音频信息
            </label>
            <label>
              <input
                type="checkbox"
                aria-label="情绪信息"
                checked={showEmotion}
                onChange={(event) => setShowEmotion(event.target.checked)}
              />
              情绪信息
            </label>
            <select
              aria-label="分组方式"
              value={groupMode}
              onChange={(event) => setGroupMode(event.target.value as GroupMode)}
            >
              <option value="none">不分组</option>
              <option value="source">按来源</option>
              <option value="audioType">按类型</option>
            </select>
            <button
              onClick={() => setEmotionDialogOpen(true)}
              disabled={selected.length === 0}
              aria-disabled={selected.length === 0}
            >
              情绪识别
            </button>
            <button onClick={() => void handleBatchDelete()} disabled={selected.length === 0 || deleting}>
              批量删除
            </button>
            {editError && <div role="alert">{editError}</div>}
          </div>

          <table
            style={{ width: '100%', borderCollapse: 'collapse', tableLayout: 'fixed' }}
          >
            {/* 固定短列/次要列宽度，让「文本」列（不设宽度）占满剩余宽度；列显隐同步 colgroup */}
            <colgroup>
              <col style={{ width: '2.5rem' }} />
              {showAudioInfo &&
                AUDIO_COLUMNS.map((column) => (
                  <col key={column.key} style={{ width: column.width }} />
                ))}
              <col />
              {showEmotion &&
                EMOTION_COLUMNS.map((column) => (
                  <col key={column.key} style={{ width: column.width }} />
                ))}
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
                {showAudioInfo &&
                  AUDIO_COLUMNS.map((column) => (
                    <th key={column.key} style={column.truncate ? TRUNCATED_CELL_STYLE : undefined}>
                      {column.label}
                    </th>
                  ))}
                <th style={TEXT_CELL_STYLE}>文本</th>
                {showEmotion &&
                  EMOTION_COLUMNS.map((column) => (
                    <th key={column.key} style={column.truncate ? TRUNCATED_CELL_STYLE : undefined}>
                      {column.label}
                    </th>
                  ))}
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
                          <th colSpan={visibleColumnCount}>
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
                        {groupItems.map(renderRowWithWaveform)}
                      </Fragment>
                    )
                  })
                : items.map(renderRowWithWaveform)}
            </tbody>
          </table>

          {emotionDialogOpen && (
            <EmotionSubmitDialog
              datasetId={datasetId}
              itemIds={selected}
              onClose={() => setEmotionDialogOpen(false)}
              onSubmitted={() => {
                void refresh()
                onChanged?.()
              }}
            />
          )}

          {reRecognizeItem && (
            <ReRecognizeDialog
              datasetId={datasetId}
              item={reRecognizeItem}
              // 该行处于内联编辑态时带上当前草稿：「保存」与内联保存同 payload，
              // 避免未保存的 audioUri/起止时间修改被静默丢弃。
              draft={editingId === reRecognizeItem.id ? draft : undefined}
              onClose={() => setReRecognizeId(null)}
              onSaved={(updated) => {
                setItems((prev) =>
                  prev.map((value) => (value.id === updated.id ? updated : value)),
                )
                // 重新识别保存成功后，若该行仍处于内联编辑态，必须同步草稿并退出编辑，
                // 否则随后点击内联「保存」会用过期草稿覆盖刚识别并保存的文本。
                if (editingId === updated.id) {
                  setDraft({
                    audioUri: updated.audioUri,
                    text: updated.text,
                    start: formatPosition(updated.startMs),
                    end:
                      updated.endMs === null || updated.endMs === undefined
                        ? ''
                        : formatPosition(updated.endMs),
                  })
                  cancelEdit()
                }
                setReRecognizeId(null)
              }}
            />
          )}
        </>
      )}
    </div>
  )
}
