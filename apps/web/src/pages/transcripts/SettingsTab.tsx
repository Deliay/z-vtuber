import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  getTranscriptSettings,
  setRoomAutoEnqueue,
  listTranscriptRecordings,
  listTranscriptModels,
  getRoomTranscripts,
  enqueueTranscript,
  retryTranscriptTask,
  createTranscriptModel,
  updateTranscriptModel,
} from '../../api/client'
import type { components as transcriptComponents } from '../../api/schema-recording-transcript'

type TranscriptSettingsItem = transcriptComponents['schemas']['TranscriptSettingsItem']
type TranscriptModel = transcriptComponents['schemas']['TranscriptModel']
type TranscriptRecordingItem = transcriptComponents['schemas']['RecordingItem']
type RoomTranscriptView = transcriptComponents['schemas']['RoomTranscriptView']

const STATUS_LABELS: Record<string, string> = {
  PENDING: '等待中',
  RUNNING: '识别中',
  DONE: '已完成',
  FAILED: '失败',
}

function formatPosition(position?: number | null): string {
  if (position === undefined || position === null) {
    return '-'
  }
  const totalSeconds = Math.floor(position / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${String(seconds).padStart(2, '0')}`
}

const inputStyle: React.CSSProperties = {
  padding: '0.5rem',
  backgroundColor: '#334155',
  color: '#e2e8f0',
  border: '1px solid #475569',
  borderRadius: '0.25rem',
}

function normalizeConcurrency(value: string | number | null | undefined): number {
  const parsed = Number(value)
  if (!Number.isFinite(parsed) || parsed < 1) {
    return 1
  }
  return Math.floor(parsed)
}

function parseExistingTaskId(error: unknown): string | undefined {
  const apiError = error as { status?: number; serverMessage?: string }
  if (apiError.status !== 409) {
    return undefined
  }
  try {
    const body = JSON.parse(apiError.serverMessage ?? '') as { taskId?: string }
    return body.taskId
  } catch {
    return undefined
  }
}

export function SettingsTab() {
  const [settings, setSettings] = useState<TranscriptSettingsItem[]>([])
  const [models, setModels] = useState<TranscriptModel[]>([])
  const [recordings, setRecordings] = useState<TranscriptRecordingItem[]>([])
  const [loading, setLoading] = useState(true)
  const [initialized, setInitialized] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [retryAction, setRetryAction] = useState<(() => void) | null>(null)

  const [enqueueRecordingId, setEnqueueRecordingId] = useState('')
  const [enqueueModelId, setEnqueueModelId] = useState('')
  const [enqueueMessage, setEnqueueMessage] = useState<string | null>(null)
  const [enqueueError, setEnqueueError] = useState<string | null>(null)

  const [selectedRoomId, setSelectedRoomId] = useState('')
  const [roomView, setRoomView] = useState<RoomTranscriptView | null>(null)
  const [roomError, setRoomError] = useState<string | null>(null)

  const [modelName, setModelName] = useState('')
  const [modelApiKey, setModelApiKey] = useState('')
  const [modelMaxConcurrency, setModelMaxConcurrency] = useState('1')
  const [modelEnabled, setModelEnabled] = useState(true)
  const [modelIsDefault, setModelIsDefault] = useState(false)
  const [modelMessage, setModelMessage] = useState<string | null>(null)
  const [modelError, setModelError] = useState<string | null>(null)
  const [concurrencyDrafts, setConcurrencyDrafts] = useState<Record<string, string>>({})

  const fetchAll = useCallback(async () => {
    try {
      setError(null)
      setRetryAction(null)
      setLoading(true)
      const [settingsData, modelsData, recordingsData] = await Promise.all([
        getTranscriptSettings(),
        listTranscriptModels(),
        listTranscriptRecordings(),
      ])
      setSettings(settingsData)
      setModels(modelsData)
      setRecordings(recordingsData)
    } catch {
      setError('服务不可用')
      setRetryAction(null)
    } finally {
      setLoading(false)
      setInitialized(true)
    }
  }, [])

  useEffect(() => {
    fetchAll()
  }, [fetchAll])

  const enabledModels = useMemo(
    () => models.filter((model) => model.enabled !== false),
    [models],
  )

  const roomOptions = useMemo(() => {
    const options: { roomId: number; label: string }[] = []
    const seen = new Set<number>()
    for (const item of settings) {
      if (typeof item.roomId === 'number' && !seen.has(item.roomId)) {
        seen.add(item.roomId)
        options.push({
          roomId: item.roomId,
          label: item.anchorName ? `${item.anchorName}（${item.roomId}）` : String(item.roomId),
        })
      }
    }
    for (const recording of recordings) {
      if (typeof recording.roomId === 'number' && !seen.has(recording.roomId)) {
        seen.add(recording.roomId)
        options.push({ roomId: recording.roomId, label: String(recording.roomId) })
      }
    }
    return options
  }, [settings, recordings])

  const handleToggleAuto = async (item: TranscriptSettingsItem) => {
    if (typeof item.roomId !== 'number') {
      return
    }
    try {
      setError(null)
      setRetryAction(null)
      await setRoomAutoEnqueue(item.roomId, !item.enabled)
      await fetchAll()
    } catch {
      setError('自动识别设置更新失败')
      setRetryAction(() => () => {
        void handleToggleAuto(item)
      })
    }
  }

  const retryFailedAction = () => {
    const action = retryAction ?? fetchAll
    setError(null)
    setRetryAction(null)
    void action()
  }

  const handleEnqueue = async (e: React.FormEvent) => {
    e.preventDefault()
    setEnqueueMessage(null)
    setEnqueueError(null)

    if (!enqueueRecordingId || !enqueueModelId) {
      setEnqueueError('请选择录播场次与模型')
      return
    }

    try {
      const result = await enqueueTranscript(enqueueRecordingId, enqueueModelId)
      setEnqueueMessage(`已创建任务 ${result.taskId}`)
      if (selectedRoomId) {
        const view = await getRoomTranscripts(Number(selectedRoomId))
        setRoomView(view)
      }
    } catch (err) {
      const existingTaskId = parseExistingTaskId(err)
      if (existingTaskId) {
        setEnqueueMessage(`已有任务 ${existingTaskId}`)
        return
      }
      setEnqueueError('发起识别失败')
    }
  }

  const handleQueryRoom = async () => {
    if (!selectedRoomId) {
      setRoomError('请选择主播')
      return
    }
    try {
      setRoomError(null)
      const view = await getRoomTranscripts(Number(selectedRoomId))
      setRoomView(view)
    } catch {
      setRoomError('场次任务加载失败')
    }
  }

  const handleRetry = async (taskId: string) => {
    try {
      await retryTranscriptTask(taskId)
      if (selectedRoomId) {
        const view = await getRoomTranscripts(Number(selectedRoomId))
        setRoomView(view)
      }
    } catch {
      setRoomError('重试失败')
    }
  }

  const handleCreateModel = async (e: React.FormEvent) => {
    e.preventDefault()
    setModelMessage(null)
    setModelError(null)

    if (!modelName.trim()) {
      setModelError('请输入模型名称')
      return
    }
    if (!modelApiKey.trim()) {
      setModelError('请输入 API Key')
      return
    }

    try {
      await createTranscriptModel({
        name: modelName,
        provider: 'ali-fun-asr',
        type: 'streaming',
        apiKey: modelApiKey,
        enabled: modelEnabled,
        isDefault: modelIsDefault,
        maxConcurrency: normalizeConcurrency(modelMaxConcurrency),
      })
      setModelName('')
      setModelApiKey('')
      setModelMaxConcurrency('1')
      setModelEnabled(true)
      setModelIsDefault(false)
      setModelMessage('模型已添加')
      const modelsData = await listTranscriptModels()
      setModels(modelsData)
    } catch {
      setModelError('添加模型失败')
    }
  }

  const handleUpdateModel = async (
    model: TranscriptModel,
    patch: transcriptComponents['schemas']['TranscriptModelUpdateRequest'],
  ) => {
    if (!model.id) {
      return
    }
    try {
      await updateTranscriptModel(model.id, patch)
      const modelsData = await listTranscriptModels()
      setModels(modelsData)
    } catch {
      setModelError('模型更新失败')
    }
  }

  if (loading && !initialized) {
    return <p>加载中...</p>
  }

  return (
    <div>
      {error && (
        <div
          role="alert"
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '0.75rem',
            border: '1px solid #7f1d1d',
            backgroundColor: '#450a0a',
            padding: '0.5rem 0.75rem',
            borderRadius: '0.25rem',
            marginBottom: '1rem',
          }}
        >
          <span style={{ color: '#f87171' }}>{error}</span>
          <button onClick={retryFailedAction} style={{ padding: '0.25rem 0.75rem' }}>
            重试
          </button>
        </div>
      )}

      <section style={{ marginBottom: '2rem' }}>
        <h3>识别设置</h3>

        {settings.length === 0 ? (
          <p>暂无自动识别配置</p>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', marginBottom: '1rem' }}>
            <thead>
              <tr>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>主播</th>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>自动识别</th>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
              </tr>
            </thead>
            <tbody>
              {settings.map((item) => (
                <tr key={item.roomId}>
                  <td style={{ padding: '0.5rem' }}>
                    {item.anchorName ?? '-'}（{item.roomId}）
                  </td>
                  <td style={{ padding: '0.5rem' }}>{item.enabled ? '已开启' : '已关闭'}</td>
                  <td style={{ padding: '0.5rem' }}>
                    <button
                      onClick={() => handleToggleAuto(item)}
                      style={{ padding: '0.25rem 0.5rem' }}
                    >
                      {item.enabled ? '关闭' : '开启'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <h4>手动发起识别</h4>
        <form onSubmit={handleEnqueue} style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', alignItems: 'center' }}>
          <select
            aria-label="录播场次"
            value={enqueueRecordingId}
            onChange={(e) => setEnqueueRecordingId(e.target.value)}
            style={inputStyle}
          >
            <option value="">选择录播场次</option>
            {recordings.map((recording) => (
              <option key={recording.id} value={recording.id}>
                {recording.title ?? recording.id}
              </option>
            ))}
          </select>

          <select
            aria-label="识别模型"
            value={enqueueModelId}
            onChange={(e) => setEnqueueModelId(e.target.value)}
            style={inputStyle}
          >
            <option value="">选择模型</option>
            {enabledModels.map((model) => (
              <option key={model.id} value={model.id}>
                {model.name}
              </option>
            ))}
          </select>

          <button type="submit" style={{ padding: '0.5rem 1rem' }}>
            发起识别
          </button>
        </form>
        {enqueueMessage && <p style={{ color: '#22c55e' }}>{enqueueMessage}</p>}
        {enqueueError && <p style={{ color: '#f87171' }}>{enqueueError}</p>}

        <h4>场次任务</h4>
        <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', marginBottom: '0.5rem' }}>
          <select
            aria-label="主播"
            value={selectedRoomId}
            onChange={(e) => setSelectedRoomId(e.target.value)}
            style={inputStyle}
          >
            <option value="">选择主播</option>
            {roomOptions.map((option) => (
              <option key={option.roomId} value={String(option.roomId)}>
                {option.label}
              </option>
            ))}
          </select>
          <button onClick={handleQueryRoom} style={{ padding: '0.5rem 1rem' }}>
            查询
          </button>
        </div>
        {roomError && <p style={{ color: '#f87171' }}>{roomError}</p>}
        {roomView && (
          <div>
            <p style={{ color: '#94a3b8' }}>
              自动识别：{roomView.autoEnqueueEnabled ? '已开启' : '已关闭'}
            </p>
            {roomView.items.length === 0 ? (
              <p>暂无场次</p>
            ) : (
              roomView.items.map((item) => (
                <div
                  key={item.recordingId}
                  style={{ border: '1px solid #334155', borderRadius: '0.25rem', padding: '0.5rem', marginBottom: '0.5rem' }}
                >
                  <div>{item.title ?? item.recordingId}</div>
                  {item.tasks.length === 0 ? (
                    <p style={{ margin: 0, color: '#94a3b8' }}>暂无任务</p>
                  ) : (
                    <ul style={{ margin: 0, paddingLeft: '1rem' }}>
                      {item.tasks.map((task) => (
                        <li key={task.taskId}>
                          {task.modelName ?? task.modelId} -{' '}
                          {STATUS_LABELS[task.status ?? ''] ?? task.status} -{' '}
                          {formatPosition(task.position)}
                          {task.status === 'FAILED' && (
                            <button
                              onClick={() => handleRetry(task.taskId ?? '')}
                              style={{ marginLeft: '0.5rem', padding: '0.25rem 0.5rem' }}
                            >
                              重试
                            </button>
                          )}
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ))
            )}
          </div>
        )}
      </section>

      <section>
        <h3>模型设置</h3>

        {models.length === 0 ? (
          <p>暂无模型</p>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', marginBottom: '1rem' }}>
            <thead>
              <tr>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>名称</th>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>提供方</th>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>状态</th>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>最大并发</th>
                <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
              </tr>
            </thead>
            <tbody>
              {models.map((model) => (
                <tr key={model.id}>
                  <td style={{ padding: '0.5rem' }}>
                    {model.name}
                    {model.isDefault ? '（默认）' : ''}
                  </td>
                  <td style={{ padding: '0.5rem' }}>{model.provider}</td>
                  <td style={{ padding: '0.5rem' }}>{model.enabled ? '已启用' : '已禁用'}</td>
                  <td style={{ padding: '0.5rem' }}>
                    <input
                      aria-label={`最大并发-${model.name}`}
                      type="number"
                      min={1}
                      value={concurrencyDrafts[model.id ?? ''] ?? String(model.maxConcurrency ?? 1)}
                      onChange={(e) =>
                        setConcurrencyDrafts((prev) => ({
                          ...prev,
                          [model.id ?? '']: e.target.value,
                        }))
                      }
                      style={{ ...inputStyle, width: '5rem' }}
                    />
                    <button
                      onClick={() =>
                        handleUpdateModel(model, {
                          maxConcurrency: normalizeConcurrency(
                            concurrencyDrafts[model.id ?? ''] ?? model.maxConcurrency,
                          ),
                        })
                      }
                      style={{ marginLeft: '0.5rem', padding: '0.25rem 0.5rem' }}
                    >
                      保存
                    </button>
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    <button
                      onClick={() => handleUpdateModel(model, { enabled: !model.enabled })}
                      style={{ marginRight: '0.5rem', padding: '0.25rem 0.5rem' }}
                    >
                      {model.enabled ? '禁用' : '启用'}
                    </button>
                    {!model.isDefault && (
                      <button
                        onClick={() => handleUpdateModel(model, { isDefault: true })}
                        style={{ padding: '0.25rem 0.5rem' }}
                      >
                        设为默认
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <h4>添加模型</h4>
        <form onSubmit={handleCreateModel} style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', alignItems: 'center' }}>
          <input
            aria-label="模型名称"
            placeholder="模型名称"
            value={modelName}
            onChange={(e) => setModelName(e.target.value)}
            style={inputStyle}
          />
          <input
            aria-label="API Key"
            placeholder="API Key"
            type="password"
            value={modelApiKey}
            onChange={(e) => setModelApiKey(e.target.value)}
            style={inputStyle}
          />
          <input
            aria-label="最大并发数"
            type="number"
            min={1}
            value={modelMaxConcurrency}
            onChange={(e) => setModelMaxConcurrency(e.target.value)}
            style={{ ...inputStyle, width: '6rem' }}
          />
          <label style={{ display: 'flex', alignItems: 'center', gap: '0.25rem' }}>
            <input
              type="checkbox"
              checked={modelEnabled}
              onChange={(e) => setModelEnabled(e.target.checked)}
            />
            启用
          </label>
          <label style={{ display: 'flex', alignItems: 'center', gap: '0.25rem' }}>
            <input
              type="checkbox"
              checked={modelIsDefault}
              onChange={(e) => setModelIsDefault(e.target.checked)}
            />
            设为默认
          </label>
          <button type="submit" style={{ padding: '0.5rem 1rem' }}>
            添加
          </button>
        </form>
        {modelMessage && <p style={{ color: '#22c55e' }}>{modelMessage}</p>}
        {modelError && <p style={{ color: '#f87171' }}>{modelError}</p>}
      </section>
    </div>
  )
}
