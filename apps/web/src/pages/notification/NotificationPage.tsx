import { useEffect, useState } from 'react'
import {
  listNotifyGroups,
  listNotificationBindings,
  createNotificationBinding,
  deleteNotificationBinding,
} from '../../api/client'
import type { components } from '../../api/schema-notify'

type NotifyGroup = components['schemas']['NotifyGroup']
type NotificationSubscription = components['schemas']['NotificationSubscription']

export function NotificationPage() {
  const [bindings, setBindings] = useState<NotificationSubscription[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [showCreateModal, setShowCreateModal] = useState(false)
  const [groups, setGroups] = useState<NotifyGroup[]>([])
  const [groupsLoading, setGroupsLoading] = useState(false)
  const [groupsError, setGroupsError] = useState<string | null>(null)
  const [selectedGroup, setSelectedGroup] = useState<string>('')
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)

  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null)

  const fetchBindings = async () => {
    try {
      setLoading(true)
      setError(null)
      const data = await listNotificationBindings()
      setBindings(data)
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchBindings()
  }, [])

  const handleOpenCreateModal = async () => {
    setShowCreateModal(true)
    setGroupsLoading(true)
    setGroupsError(null)
    setSelectedGroup('')
    setSubmitError(null)

    try {
      const data = await listNotifyGroups()
      setGroups(data)
    } catch {
      setGroupsError('加载群列表失败')
    } finally {
      setGroupsLoading(false)
    }
  }

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault()
    setSubmitError(null)

    if (!selectedGroup) {
      setSubmitError('请选择群')
      return
    }

    const group = groups.find((g) => g.groupId === selectedGroup)
    if (!group) {
      setSubmitError('群不存在')
      return
    }

    try {
      setSubmitting(true)
      await createNotificationBinding({
        eventType: 'cookie_invalid',
        groupId: group.groupId,
        groupName: group.groupName,
      })
      setShowCreateModal(false)
      await fetchBindings()
    } catch (err: any) {
      if (err.status === 400 && err.serverMessage) {
        setSubmitError(err.serverMessage)
      } else if (err.serverMessage?.includes('已绑定')) {
        setSubmitError('该群已绑定')
      } else {
        setSubmitError('创建失败，请重试')
      }
    } finally {
      setSubmitting(false)
    }
  }

  const handleDelete = async (id: string) => {
    try {
      await deleteNotificationBinding(id)
      await fetchBindings()
    } catch (err: any) {
      if (err.status === 404) {
        setSubmitError('该绑定已不存在')
        await fetchBindings()
      } else {
        setSubmitError('删除失败，请重试')
      }
    }
    setConfirmingDelete(null)
  }

  const formatDate = (dateString?: string | null) => {
    if (!dateString) return '-'
    return new Date(dateString).toLocaleString()
  }

  return (
    <div>
      <h2>通知管理</h2>

      <div style={{ marginBottom: '1rem' }}>
        <button onClick={handleOpenCreateModal} style={{ padding: '0.5rem 1rem' }}>
          新增绑定
        </button>
      </div>

      {loading ? (
        <p>加载中...</p>
      ) : error ? (
        <p style={{ color: '#f87171' }}>{error}</p>
      ) : bindings.length === 0 ? (
        <p>暂无通知绑定</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>通知类型</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>目标群</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>绑定时间</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
            </tr>
          </thead>
          <tbody>
            {bindings.map((binding) => {
              const isConfirming = confirmingDelete === binding.id

              return (
                <tr key={binding.id}>
                  <td style={{ padding: '0.5rem' }}>
                    {binding.eventType === 'cookie_invalid' ? 'Cookie 失效' : binding.eventType}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {binding.groupName ?? binding.groupId}（{binding.groupId}）
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {formatDate(binding.createdAt)}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {isConfirming ? (
                      <span style={{ display: 'flex', gap: '0.5rem' }}>
                        <button
                          onClick={() => handleDelete(binding.id!)}
                          style={{ padding: '0.25rem 0.5rem' }}
                        >
                          确认删除
                        </button>
                        <button
                          onClick={() => setConfirmingDelete(null)}
                          style={{ padding: '0.25rem 0.5rem' }}
                        >
                          返回
                        </button>
                      </span>
                    ) : (
                      <button
                        onClick={() => setConfirmingDelete(binding.id!)}
                        style={{ padding: '0.25rem 0.5rem' }}
                      >
                        删除
                      </button>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}

      {showCreateModal && (
        <div
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
          }}
        >
          <div
            style={{
              backgroundColor: 'white',
              padding: '1.5rem',
              borderRadius: '0.5rem',
              minWidth: '400px',
            }}
          >
            <h3>新增绑定</h3>

            <form onSubmit={handleCreate}>
              <div style={{ marginBottom: '1rem' }}>
                <label style={{ display: 'block', marginBottom: '0.5rem' }}>
                  通知类型
                </label>
                <input
                  type="text"
                  value="Cookie 失效"
                  disabled
                  style={{ width: '100%', padding: '0.5rem' }}
                />
              </div>

              <div style={{ marginBottom: '1rem' }}>
                <label style={{ display: 'block', marginBottom: '0.5rem' }}>
                  选择群
                </label>
                {groupsLoading ? (
                  <p>加载群列表中...</p>
                ) : groupsError ? (
                  <p style={{ color: '#f87171' }}>{groupsError}</p>
                ) : (
                  <select
                    value={selectedGroup}
                    onChange={(e) => setSelectedGroup(e.target.value)}
                    style={{ width: '100%', padding: '0.5rem' }}
                  >
                    <option value="">请选择群</option>
                    {groups.map((group) => (
                      <option key={group.groupId} value={group.groupId}>
                        {group.groupName ?? group.groupId}（{group.groupId}）
                      </option>
                    ))}
                  </select>
                )}
              </div>

              {submitError && (
                <p style={{ color: '#f87171', marginBottom: '1rem' }}>
                  {submitError}
                </p>
              )}

              <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end' }}>
                <button
                  type="button"
                  onClick={() => setShowCreateModal(false)}
                  style={{ padding: '0.5rem 1rem' }}
                >
                  取消
                </button>
                <button
                  type="submit"
                  disabled={submitting || channelsLoading}
                  style={{ padding: '0.5rem 1rem' }}
                >
                  {submitting ? '创建中...' : '创建'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  )
}
