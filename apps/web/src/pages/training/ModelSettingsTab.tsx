import { useCallback, useEffect, useState, type CSSProperties } from 'react'
import {
  apiErrorMessage,
  createTrainingModel,
  deleteTrainingModel,
  deleteTrainingProvider,
  discoverTrainingModels,
  importTrainingModels,
  listTrainingModels,
  listTrainingProviders,
  updateTrainingModel,
} from '../../api/client'
import { ProviderDialog } from './ProviderDialog'
import type { components } from '../../api/schema-training'

type TrainingProvider = components['schemas']['TrainingProvider']
type TrainingModel = components['schemas']['TrainingModel']

const inputStyle: CSSProperties = {
  padding: '0.5rem',
  backgroundColor: '#334155',
  color: '#e2e8f0',
  border: '1px solid #475569',
  borderRadius: '0.25rem',
}

const cellStyle: CSSProperties = { textAlign: 'left', padding: '0.5rem' }

const overlayStyle: CSSProperties = {
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
}

const panelStyle: CSSProperties = {
  backgroundColor: '#1e293b',
  color: '#e2e8f0',
  padding: '1.5rem',
  borderRadius: '0.5rem',
  minWidth: '400px',
  maxWidth: '90vw',
  maxHeight: '90vh',
  overflow: 'auto',
}

/**
 * 模型设置：Providers 双表管理（新增/编辑/删除/获取模型导入）+ Models 管理
 * （slug 展示、手动添加、启停、删除）。API key 只进不出、永不回显。
 */
export function ModelSettingsTab() {
  const [providers, setProviders] = useState<TrainingProvider[]>([])
  const [models, setModels] = useState<TrainingModel[]>([])
  const [error, setError] = useState('')

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingProvider, setEditingProvider] = useState<TrainingProvider | undefined>(undefined)

  const [discoverTarget, setDiscoverTarget] = useState<TrainingProvider | null>(null)
  const [discovered, setDiscovered] = useState<string[]>([])
  const [discoverSelected, setDiscoverSelected] = useState<string[]>([])
  const [discoverBusy, setDiscoverBusy] = useState(false)
  /** 导入选中模型请求进行中：此时忽略遮罩关闭并禁用关闭按钮，保住结果反馈。 */
  const [discoverImporting, setDiscoverImporting] = useState(false)
  const [discoverError, setDiscoverError] = useState('')
  const [discoverResult, setDiscoverResult] = useState<{ imported: number; skipped: number } | null>(null)

  const [manualOpen, setManualOpen] = useState(false)
  const [manualProviderId, setManualProviderId] = useState('')
  const [manualModel, setManualModel] = useState('')
  const [manualError, setManualError] = useState('')

  const refresh = useCallback(async () => {
    try {
      const [providerList, modelList] = await Promise.all([
        listTrainingProviders(),
        listTrainingModels(),
      ])
      setProviders(providerList)
      setModels(modelList)
      setError('')
    } catch (err) {
      setError(apiErrorMessage(err, '加载模型设置失败'))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const openCreate = () => {
    setEditingProvider(undefined)
    setDialogOpen(true)
  }

  const openEdit = (provider: TrainingProvider) => {
    setEditingProvider(provider)
    setDialogOpen(true)
  }

  const handleDeleteProvider = async (provider: TrainingProvider) => {
    const childCount = models.filter((model) => model.providerId === provider.id).length
    const confirmed = window.confirm(
      `确认删除 Provider「${provider.name}」？其下 ${childCount} 个模型将一并删除，不可恢复。`,
    )
    if (!confirmed) return
    try {
      await deleteTrainingProvider(provider.id)
      await refresh()
    } catch (err) {
      setError(apiErrorMessage(err, '删除 Provider 失败'))
    }
  }

  const handleDiscover = async (provider: TrainingProvider) => {
    setDiscoverTarget(provider)
    setDiscovered([])
    setDiscoverSelected([])
    setDiscoverError('')
    setDiscoverResult(null)
    setDiscoverImporting(false)
    setDiscoverBusy(true)
    try {
      const list = await discoverTrainingModels(provider.id)
      setDiscovered(list.map((item) => item.model))
    } catch (err) {
      // 保留服务端错误码（不可达 / 响应非法 / Provider 不存在 等需区分）。
      setDiscoverError(apiErrorMessage(err, '模型服务不可用'))
    } finally {
      setDiscoverBusy(false)
    }
  }

  const handleImport = async () => {
    if (!discoverTarget || discoverSelected.length === 0 || discoverBusy) return
    setDiscoverBusy(true)
    setDiscoverImporting(true)
    setDiscoverError('')
    try {
      const result = await importTrainingModels(discoverTarget.id, discoverSelected)
      setDiscoverResult(result)
      await refresh()
    } catch (err) {
      setDiscoverError(apiErrorMessage(err, '导入模型失败'))
    } finally {
      setDiscoverBusy(false)
      setDiscoverImporting(false)
    }
  }

  const handleToggleModel = async (model: TrainingModel) => {
    try {
      await updateTrainingModel(model.id, { enabled: !model.enabled })
      await refresh()
    } catch (err) {
      setError(apiErrorMessage(err, '更新模型失败'))
    }
  }

  const handleDeleteModel = async (model: TrainingModel) => {
    if (!window.confirm(`确认删除模型「${model.slug}」？`)) return
    try {
      await deleteTrainingModel(model.id)
      await refresh()
    } catch (err) {
      setError(apiErrorMessage(err, '删除模型失败'))
    }
  }

  const handleCreateModel = async () => {
    if (!manualProviderId) {
      setManualError('请选择 Provider')
      return
    }
    try {
      await createTrainingModel({ providerId: manualProviderId, model: manualModel.trim() })
      setManualOpen(false)
      setManualModel('')
      setManualProviderId('')
      setManualError('')
      await refresh()
    } catch (err) {
      setManualError(apiErrorMessage(err, '添加模型失败'))
    }
  }

  return (
    <div className="model-settings-tab">
      {error && <div role="alert">{error}</div>}

      <section style={{ marginBottom: '2rem' }}>
        <h3>Providers</h3>
        <button onClick={openCreate}>新增 Provider</button>

        {providers.length === 0 && <p>请先添加 Provider 并获取/添加模型</p>}

        {providers.length > 0 && (
          <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
            <thead>
              <tr>
                <th style={cellStyle}>名称</th>
                <th style={cellStyle}>模型类型</th>
                <th style={cellStyle}>Endpoint</th>
                <th style={cellStyle}>API key 状态</th>
                <th style={cellStyle}>操作</th>
              </tr>
            </thead>
            <tbody>
              {providers.map((provider) => (
                <tr key={provider.id}>
                  <td style={cellStyle}>{provider.name}</td>
                  <td style={cellStyle}>{provider.type}</td>
                  <td style={cellStyle}>{provider.endpoint}</td>
                  <td style={cellStyle}>{provider.apiKeySet ? '已配置' : '未配置'}</td>
                  <td style={cellStyle}>
                    <button
                      aria-label={`获取模型 ${provider.name}`}
                      onClick={() => void handleDiscover(provider)}
                    >
                      获取模型
                    </button>
                    <button
                      aria-label={`编辑 ${provider.name}`}
                      onClick={() => openEdit(provider)}
                      style={{ marginLeft: '0.5rem' }}
                    >
                      编辑
                    </button>
                    <button
                      aria-label={`删除 ${provider.name}`}
                      onClick={() => void handleDeleteProvider(provider)}
                      style={{ marginLeft: '0.5rem' }}
                    >
                      删除
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section>
        <h3>Models</h3>
        <button onClick={() => setManualOpen(true)}>手动添加模型</button>

        {/* Provider 已存在但尚无模型时同样给出引导（最常见的中间态）。 */}
        {models.length === 0 && (
          <p>暂无模型，请通过「获取模型」导入或手动添加</p>
        )}

        {models.length > 0 && (
          <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
            <thead>
              <tr>
                <th style={cellStyle}>slug</th>
                <th style={cellStyle}>所属 Provider</th>
                <th style={cellStyle}>启用</th>
                <th style={cellStyle}>操作</th>
              </tr>
            </thead>
            <tbody>
              {models.map((model) => (
                <tr key={model.id}>
                  <td style={cellStyle}>{model.slug}</td>
                  <td style={cellStyle}>{model.providerName}</td>
                  <td style={cellStyle}>{model.enabled ? '已启用' : '已停用'}</td>
                  <td style={cellStyle}>
                    <button
                      aria-label={`${model.enabled ? '停用' : '启用'} ${model.slug}`}
                      onClick={() => void handleToggleModel(model)}
                    >
                      {model.enabled ? '停用' : '启用'}
                    </button>
                    <button
                      aria-label={`删除 ${model.slug}`}
                      onClick={() => void handleDeleteModel(model)}
                      style={{ marginLeft: '0.5rem' }}
                    >
                      删除
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {dialogOpen && (
        <ProviderDialog
          provider={editingProvider}
          onClose={() => setDialogOpen(false)}
          onSaved={() => void refresh()}
        />
      )}

      {discoverTarget && (
        <div
          style={overlayStyle}
          // 导入进行中忽略遮罩关闭，避免卸载弹窗丢失「已导入/跳过」结果反馈
          onClick={() => {
            if (!discoverImporting) setDiscoverTarget(null)
          }}
        >
          <div
            role="dialog"
            aria-label={`获取模型 - ${discoverTarget.name}`}
            aria-modal="true"
            style={panelStyle}
            onClick={(event) => event.stopPropagation()}
          >
            <h3>获取模型 - {discoverTarget.name}</h3>

            {discoverBusy && discovered.length === 0 && <div>加载中...</div>}
            {discoverError && <div role="alert">{discoverError}</div>}

            {discovered.map((model) => (
              <label key={model} style={{ display: 'block' }}>
                <input
                  type="checkbox"
                  aria-label={`导入 ${model}`}
                  checked={discoverSelected.includes(model)}
                  onChange={() =>
                    setDiscoverSelected((prev) =>
                      prev.includes(model)
                        ? prev.filter((value) => value !== model)
                        : [...prev, model],
                    )
                  }
                />
                {model}
              </label>
            ))}

            {!discoverBusy && !discoverError && discovered.length === 0 && (
              <div style={{ color: '#94a3b8' }}>未获取到模型</div>
            )}

            {discoverResult && (
              <div role="status">
                已导入 {discoverResult.imported} 个，跳过 {discoverResult.skipped} 个
              </div>
            )}

            <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem' }}>
              <button
                onClick={() => void handleImport()}
                disabled={discoverSelected.length === 0 || discoverBusy}
              >
                导入选中
              </button>
              <button
                onClick={() => setDiscoverTarget(null)}
                disabled={discoverImporting}
              >
                关闭
              </button>
            </div>
          </div>
        </div>
      )}

      {manualOpen && (
        <div style={overlayStyle} onClick={() => setManualOpen(false)}>
          <div
            role="dialog"
            aria-label="手动添加模型"
            aria-modal="true"
            style={panelStyle}
            onClick={(event) => event.stopPropagation()}
          >
            <h3>手动添加模型</h3>

            <label htmlFor="manual-provider" style={{ display: 'block', marginBottom: '0.5rem' }}>
              所属 Provider
              <select
                id="manual-provider"
                aria-label="所属 Provider"
                style={{ ...inputStyle, display: 'block', width: '100%' }}
                value={manualProviderId}
                onChange={(event) => setManualProviderId(event.target.value)}
              >
                <option value="">请选择 Provider</option>
                {providers.map((provider) => (
                  <option key={provider.id} value={provider.id}>
                    {provider.name}
                  </option>
                ))}
              </select>
            </label>

            <label htmlFor="manual-model" style={{ display: 'block', marginBottom: '0.5rem' }}>
              模型名
              <input
                id="manual-model"
                aria-label="模型名"
                style={{ ...inputStyle, display: 'block', width: '100%' }}
                value={manualModel}
                onChange={(event) => setManualModel(event.target.value)}
              />
            </label>

            {manualError && <div role="alert">{manualError}</div>}

            <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem' }}>
              <button onClick={() => void handleCreateModel()}>确认添加</button>
              <button onClick={() => setManualOpen(false)}>取消</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
