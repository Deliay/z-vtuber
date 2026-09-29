import { useState, type CSSProperties } from 'react'
import {
  apiErrorMessage,
  createTrainingProvider,
  updateTrainingProvider,
} from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingProvider = components['schemas']['TrainingProvider']

export interface ProviderDialogProps {
  /** 传入即为编辑模式（apiKey 留空表示不修改）。 */
  provider?: TrainingProvider
  onClose: () => void
  /** 保存成功后回调（用于刷新列表）。 */
  onSaved: () => void
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

const fieldStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.25rem',
  marginBottom: '0.75rem',
}

/**
 * Provider 新增/编辑弹窗：名称、类型（仅 OpenAI / Claude）、Endpoint、API key。
 * API key 明文永不回显（服务端亦不回传）；编辑时留空表示不修改。
 */
export function ProviderDialog({ provider, onClose, onSaved }: ProviderDialogProps) {
  const editing = provider !== undefined
  const [name, setName] = useState(provider?.name ?? '')
  const [type, setType] = useState(provider?.type ?? 'openai')
  const [endpoint, setEndpoint] = useState(provider?.endpoint ?? '')
  const [apiKey, setApiKey] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const handleSave = async () => {
    if (busy) return
    setBusy(true)
    setError('')
    try {
      if (editing && provider) {
        const body: components['schemas']['TrainingProviderUpdateRequest'] = {
          name: name.trim(),
          type,
          endpoint: endpoint.trim(),
        }
        // API key 留空即不修改（不回显、不覆盖）。
        if (apiKey.trim() !== '') body.apiKey = apiKey
        await updateTrainingProvider(provider.id, body)
      } else {
        await createTrainingProvider({
          name: name.trim(),
          type,
          endpoint: endpoint.trim(),
          apiKey,
        })
      }
      onSaved()
      onClose()
    } catch (err) {
      setError(apiErrorMessage(err, '保存 Provider 失败'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div
      className="provider-dialog-overlay"
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
        aria-label={editing ? '编辑 Provider' : '新增 Provider'}
        aria-modal="true"
        className="provider-dialog"
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
        <h3>{editing ? '编辑 Provider' : '新增 Provider'}</h3>

        <label htmlFor="provider-name" style={fieldStyle}>
          名称
          <input
            id="provider-name"
            aria-label="名称"
            style={inputStyle}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </label>

        <label htmlFor="provider-type" style={fieldStyle}>
          模型类型
          <select
            id="provider-type"
            aria-label="模型类型"
            style={inputStyle}
            value={type}
            onChange={(event) => setType(event.target.value)}
          >
            <option value="openai">OpenAI</option>
            <option value="claude">Claude</option>
          </select>
        </label>

        <label htmlFor="provider-endpoint" style={fieldStyle}>
          Endpoint
          <input
            id="provider-endpoint"
            aria-label="Endpoint"
            style={inputStyle}
            value={endpoint}
            onChange={(event) => setEndpoint(event.target.value)}
          />
        </label>

        <label htmlFor="provider-api-key" style={fieldStyle}>
          API key
          <input
            id="provider-api-key"
            aria-label="API key"
            type="password"
            style={inputStyle}
            value={apiKey}
            onChange={(event) => setApiKey(event.target.value)}
          />
        </label>

        {editing && (
          <div style={{ color: '#94a3b8', fontSize: '0.85rem' }}>
            API key 留空表示不修改，保存后不会回显
          </div>
        )}

        {error && <div role="alert">{error}</div>}

        <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem' }}>
          <button onClick={() => void handleSave()} disabled={busy}>
            保存
          </button>
          <button onClick={onClose} disabled={busy}>
            取消
          </button>
        </div>
      </div>
    </div>
  )
}
