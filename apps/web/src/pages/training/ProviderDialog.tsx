import { useState } from 'react'
import { Alert, Form, Input, Modal, Select } from 'antd'
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
    <Modal
      title={editing ? '编辑 Provider' : '新增 Provider'}
      open
      onOk={handleSave}
      onCancel={onClose}
      confirmLoading={busy}
      okText="保存"
      cancelText="取消"
      maskClosable={false}
      destroyOnHidden
    >
      <Form layout="vertical">
        <Form.Item label="名称">
          <Input aria-label="名称" value={name} onChange={(e) => setName(e.target.value)} />
        </Form.Item>
        <Form.Item label="模型类型">
          <Select
            aria-label="模型类型"
            value={type}
            onChange={setType}
            options={[
              { value: 'openai', label: 'OpenAI' },
              { value: 'claude', label: 'Claude' },
            ]}
          />
        </Form.Item>
        <Form.Item label="Endpoint">
          <Input
            aria-label="Endpoint"
            value={endpoint}
            onChange={(e) => setEndpoint(e.target.value)}
          />
        </Form.Item>
        <Form.Item
          label="API key"
          extra={editing ? 'API key 留空表示不修改，保存后不会回显' : undefined}
        >
          <Input.Password
            aria-label="API key"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
          />
        </Form.Item>
      </Form>

      {error && <Alert type="error" showIcon message={error} />}
    </Modal>
  )
}
