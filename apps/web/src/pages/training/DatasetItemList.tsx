import { useCallback, useEffect, useMemo, useState, type MouseEvent } from 'react'
import {
  Alert,
  App as AntdApp,
  Button,
  Checkbox,
  Empty,
  Input,
  Select,
  Space,
  Table,
  Typography,
  type TableColumnsType,
} from 'antd'
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
import { TruncatedText } from '../../components/TruncatedText'
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

/** 合成分组头行（非真实条目，仅用于在表格中承载分组标题与全选）。 */
interface GroupRow {
  __group: true
  key: string
  groupKey: string
  label: string
  items: TrainingDatasetItem[]
}

type Row = TrainingDatasetItem | GroupRow

const TIME_INVALID = '时间输入无效'

function isGroupRow(row: Row): row is GroupRow {
  return (row as GroupRow).__group === true
}

/** 「音频信息」六个可选列（默认隐藏）。 */
const AUDIO_COLUMNS: {
  key: keyof TrainingDatasetItem
  label: string
  width: number
  truncate?: boolean
}[] = [
  { key: 'audioType', label: '音频类型', width: 110 },
  { key: 'source', label: '音频来源', width: 140, truncate: true },
  { key: 'audioUri', label: '音频地址', width: 200, truncate: true },
  { key: 'audioKey', label: '音频标识', width: 200, truncate: true },
  { key: 'startMs', label: '开始时间', width: 90 },
  { key: 'endMs', label: '结束时间', width: 90 },
]

/** 「情绪信息」十个可选列（默认展示）。 */
const EMOTION_COLUMNS: {
  key: keyof EmotionResult
  label: string
  width: number
  truncate?: boolean
}[] = [
  { key: 'emotionControlWeight', label: '情绪控制权重', width: 120 },
  { key: 'emotionDescription', label: '情绪描述', width: 200, truncate: true },
  { key: 'happy', label: '开心', width: 80 },
  { key: 'angry', label: '愤怒', width: 80 },
  { key: 'sad', label: '悲伤', width: 80 },
  { key: 'afraid', label: '恐惧', width: 80 },
  { key: 'disgusted', label: '厌恶', width: 80 },
  { key: 'melancholic', label: '忧郁', width: 80 },
  { key: 'surprised', label: '惊讶', width: 80 },
  { key: 'calm', label: '平静', width: 80 },
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
  const { modal } = AntdApp.useApp()
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

  const allChecked = items.length > 0 && items.every((item) => selected.includes(item.id))

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
        item.endMs === null || item.endMs === undefined ? '' : formatPosition(item.endMs),
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

  const handleBatchDelete = () => {
    if (selected.length === 0 || deleting) return
    modal.confirm({
      title: `确认删除选中的 ${selected.length} 条音频数据？`,
      content: '删除后不可恢复。',
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { danger: true },
      onOk: async () => {
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
      },
    })
  }

  const reRecognizeItem = reRecognizeId
    ? items.find((item) => item.id === reRecognizeId)
    : undefined

  const dataSource: Row[] = useMemo(() => {
    if (!groups) return items
    const rows: Row[] = []
    for (const [key, groupItems] of groups) {
      const label = groupMode === 'source' ? '来源' : '类型'
      rows.push({
        __group: true,
        key: `group-${groupMode}-${key}`,
        groupKey: key,
        label: `${label}: ${key}（${groupItems.length} 条）`,
        items: groupItems,
      })
      rows.push(...groupItems)
    }
    return rows
  }, [groups, items, groupMode])

  const expandedKeys = previewId ? [previewId] : []

  const columns: TableColumnsType<Row> = [
    {
      key: 'selection',
      width: 48,
      title: <Checkbox aria-label="全选" checked={allChecked} onChange={toggleAll} />,
      onCell: (record) =>
        isGroupRow(record) ? { colSpan: visibleColumnCount } : { colSpan: 1 },
      render: (_value, record) => {
        if (isGroupRow(record)) {
          const groupIds = record.items.map((item) => item.id)
          return (
            <Checkbox
              aria-label={`全选分组 ${record.groupKey}`}
              checked={groupIds.every((id) => selected.includes(id))}
              onChange={() => toggleGroup(groupIds)}
            >
              {record.label}
            </Checkbox>
          )
        }
        return (
          <Checkbox
            aria-label={`选择 ${record.audioKey}`}
            checked={selected.includes(record.id)}
            onChange={() => toggleItem(record.id)}
          />
        )
      },
    },
    ...(showAudioInfo
      ? AUDIO_COLUMNS.map<TableColumnsType<Row>[number]>((column) => ({
          title: column.label,
          dataIndex: column.key,
          key: column.key,
          width: column.width,
          onCell: (record) => (isGroupRow(record) ? { colSpan: 0 } : {}),
          render: (_value, record) => {
            if (isGroupRow(record)) return null
            const item = record
            const editing = editingId === item.id
            if (column.key === 'audioUri') {
              return editing ? (
                <Input
                  aria-label="音频地址"
                  value={draft.audioUri}
                  onClick={stopPropagation}
                  onChange={(event) => setDraft({ ...draft, audioUri: event.target.value })}
                />
              ) : (
                <TruncatedText text={item.audioUri || '无'} />
              )
            }
            if (column.key === 'startMs') {
              return editing ? (
                <Input
                  aria-label="开始时间"
                  value={draft.start}
                  onClick={stopPropagation}
                  onChange={(event) => setDraft({ ...draft, start: event.target.value })}
                />
              ) : (
                formatPosition(item.startMs)
              )
            }
            if (column.key === 'endMs') {
              return editing ? (
                <Input
                  aria-label="结束时间"
                  value={draft.end}
                  onClick={stopPropagation}
                  onChange={(event) => setDraft({ ...draft, end: event.target.value })}
                />
              ) : (
                formatPosition(item.endMs)
              )
            }
            if (column.key === 'audioKey') return <TruncatedText text={item.audioKey} />
            const value = String(item[column.key] ?? '-')
            return column.truncate ? <TruncatedText text={value} /> : value
          },
        }))
      : []),
    {
      title: '文本',
      dataIndex: 'text',
      key: 'text',
      ellipsis: false,
      onCell: (record) => (isGroupRow(record) ? { colSpan: 0 } : {}),
      render: (_value, record) => {
        if (isGroupRow(record)) return null
        const item = record
        const editing = editingId === item.id
        return editing ? (
          <Input.TextArea
            aria-label="文本"
            autoSize={{ minRows: 3, maxRows: 8 }}
            value={draft.text}
            onClick={stopPropagation}
            onChange={(event) => setDraft({ ...draft, text: event.target.value })}
          />
        ) : (
          item.text
        )
      },
    },
    ...(showEmotion
      ? EMOTION_COLUMNS.map<TableColumnsType<Row>[number]>((column) => ({
          title: column.label,
          key: column.key,
          width: column.width,
          onCell: (record) => (isGroupRow(record) ? { colSpan: 0 } : {}),
          render: (_value, record) => {
            if (isGroupRow(record)) return null
            const value = emotionCellValue(record.emotion, column.key).text
            return column.truncate ? <TruncatedText text={value} /> : value
          },
        }))
      : []),
    {
      title: '操作',
      key: 'action',
      width: 220,
      onCell: (record) => (isGroupRow(record) ? { colSpan: 0 } : {}),
      render: (_value, record) => {
        if (isGroupRow(record)) return null
        const item = record
        const editing = editingId === item.id
        if (editing) {
          return (
            <Space size={4} wrap>
              <Button size="small" type="primary" loading={savingEdit} onClick={() => void saveEdit(item)}>
                保存
              </Button>
              <Button size="small" onClick={cancelEdit}>
                取消
              </Button>
              <Button size="small" onClick={() => setReRecognizeId(item.id)}>
                重新识别
              </Button>
            </Space>
          )
        }
        return (
          <Button size="small" aria-label={`编辑 ${item.audioKey}`} onClick={() => startEdit(item)}>
            编辑
          </Button>
        )
      },
    },
  ]

  return (
    <div className="dataset-item-list">
      {error && <Alert type="error" showIcon title={error} style={{ marginBottom: 8 }} />}

      {items.length === 0 ? (
        <Empty description="暂无音频数据，可从 Transcript 导入" />
      ) : (
        <>
          <Space wrap style={{ marginBottom: 12 }}>
            <Typography.Text>展示类型</Typography.Text>
            <Checkbox
              aria-label="音频信息"
              checked={showAudioInfo}
              onChange={(event) => setShowAudioInfo(event.target.checked)}
            >
              音频信息
            </Checkbox>
            <Checkbox
              aria-label="情绪信息"
              checked={showEmotion}
              onChange={(event) => setShowEmotion(event.target.checked)}
            >
              情绪信息
            </Checkbox>
            <Select
              aria-label="分组方式"
              value={groupMode}
              onChange={(value) => setGroupMode(value as GroupMode)}
              style={{ width: 120 }}
              options={[
                { value: 'none', label: '不分组' },
                { value: 'source', label: '按来源' },
                { value: 'audioType', label: '按类型' },
              ]}
            />
            <Button
              disabled={selected.length === 0}
              aria-disabled={selected.length === 0}
              onClick={() => setEmotionDialogOpen(true)}
            >
              情绪识别
            </Button>
            <Button
              danger
              disabled={selected.length === 0 || deleting}
              loading={deleting}
              onClick={handleBatchDelete}
            >
              批量删除
            </Button>
            {editError && <Typography.Text type="danger">{editError}</Typography.Text>}
          </Space>

          <Table<Row>
            // 受控 `expandedRowKeys` 在当前 `@rc-component/table` 版本下切换时不会折叠旧行
            // （同一时刻会残留多行展开），用 key 让预览行变化时重建表格，保证「同一时刻仅展开一行」。
            key={previewId ?? 'no-preview'}
            rowKey={(record) => (isGroupRow(record) ? record.key : record.id)}
            columns={columns}
            dataSource={dataSource}
            pagination={false}
            tableLayout="fixed"
            expandable={{
              expandedRowKeys: expandedKeys,
              showExpandColumn: false,
              expandedRowRender: (record) =>
                isGroupRow(record) ? null : (
                  <DatasetItemWaveform item={record} loader={waveformLoader} />
                ),
            }}
            onRow={(record) =>
              isGroupRow(record)
                ? {}
                : {
                    onClick: () => setPreviewId(record.id),
                  }
            }
          />

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
