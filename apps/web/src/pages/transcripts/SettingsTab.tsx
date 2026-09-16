import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  getTranscriptSettings,
  setRoomAutoEnqueue,
  listTranscriptModels,
  createTranscriptModel,
  updateTranscriptModel,
} from '../../api/client'
import type { components as transcriptComponents } from '../../api/schema-recording-transcript'

type TranscriptSettingsItem = transcriptComponents['schemas']['TranscriptSettingsItem']
type TranscriptModel = transcriptComponents['schemas']['TranscriptModel']

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

export function SettingsTab() {
  const [settings, setSettings] = useState<TranscriptSettingsItem[]>([])
  const [models, setModels] = useState<TranscriptModel[]>([])
  const [loading, setLoading] = useState(true)
  const [initialized, setInitialized] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [retryAction, setRetryAction] = useState<(() => void) | null>(null)

  const [newRoomId, setNewRoomId] = useState('')
  const [addMessage, setAddMessage] = useState<string | null>(null)
  const [addError, setAddError] = useState<string | null>(null)

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
      const [settingsData, modelsData] = await Promise.all([
        getTranscriptSettings(),
        listTranscriptModels(),
      ])
      setSettings(settingsData)
      setModels(modelsData)
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

  const hasDefaultModel = useMemo(
    () => models.some((model) => model.isDefault === true),
    [models],
  )

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

  const handleAddAuto = async (e: React.FormEvent) => {
    e.preventDefault()
    setAddError(null)
    setAddMessage(null)

    const roomId = Number(newRoomId)
    if (!Number.isInteger(roomId) || roomId <= 0) {
      setAddError('请输入有效的直播间号')
      return
    }

    const submit = async () => {
      try {
        setError(null)
        setRetryAction(null)
        await setRoomAutoEnqueue(roomId, true)
        setNewRoomId('')
        setAddMessage('已添加自动识别')
        await fetchAll()
      } catch {
        setError('添加自动识别失败')
        setRetryAction(() => submit)
      }
    }
    await submit()
  }

  const retryFailedAction = () => {
    const action = retryAction ?? fetchAll
    setError(null)
    setRetryAction(null)
    void action()
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

        <form
          onSubmit={handleAddAuto}
          noValidate
          style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', alignItems: 'center' }}
        >
          <input
            aria-label="直播间号"
            placeholder="直播间号"
            type="number"
            min={1}
            value={newRoomId}
            onChange={(e) => setNewRoomId(e.target.value)}
            style={{ ...inputStyle, width: '9rem' }}
          />
          <button type="submit" style={{ padding: '0.5rem 1rem' }}>
            添加自动识别
          </button>
        </form>
        {addError && <p style={{ color: '#f87171' }}>{addError}</p>}
        {addMessage && <p style={{ color: '#22c55e' }}>{addMessage}</p>}
        {!hasDefaultModel && (
          <p style={{ color: '#fbbf24' }}>未配置默认模型，自动识别需要先设置默认模型</p>
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
