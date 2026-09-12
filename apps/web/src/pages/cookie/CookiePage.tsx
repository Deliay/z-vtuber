import { useEffect, useRef, useState } from 'react'
import {
  listCookieAccounts,
  createCookieAccount,
  getCookieAccount,
  deleteCookieAccount,
} from '../../api/client'
import type { components } from '../../api/schema-cookie'

type AccountItem = components['schemas']['AccountItem']
type PendingAccount = components['schemas']['PendingAccount']

const SESSION_STATUS_LABELS: Record<string, string> = {
  pending_login: '等待扫码',
  active: '活跃',
  expired: '已过期',
  invalid: '已失效',
}

const SESSION_STATUS_COLORS: Record<string, string> = {
  pending_login: '#eab308',
  active: '#22c55e',
  expired: '#94a3b8',
  invalid: '#f87171',
}

const POLL_INTERVAL = 2000

export function CookiePage() {
  const [accounts, setAccounts] = useState<AccountItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [showCreateModal, setShowCreateModal] = useState(false)
  const [pendingAccount, setPendingAccount] = useState<PendingAccount | null>(null)
  const [creating, setCreating] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [pollError, setPollError] = useState<string | null>(null)
  const [qrExpired, setQrExpired] = useState(false)

  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const fetchAccounts = async () => {
    try {
      setLoading(true)
      setError(null)
      const data = await listCookieAccounts()
      setAccounts(data)
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchAccounts()
  }, [])

  useEffect(() => {
    return () => {
      if (pollTimerRef.current) {
        clearInterval(pollTimerRef.current)
        pollTimerRef.current = null
      }
    }
  }, [])

  const startPolling = (accountId: string) => {
    if (pollTimerRef.current) {
      clearInterval(pollTimerRef.current)
    }
    setPollError(null)
    pollTimerRef.current = setInterval(async () => {
      try {
        const status = await getCookieAccount(accountId)
        if (status.status === 'active' || status.status === 'expired' || status.status === 'invalid') {
          if (pollTimerRef.current) {
            clearInterval(pollTimerRef.current)
            pollTimerRef.current = null
          }
          setShowCreateModal(false)
          setPendingAccount(null)
          setPollError(null)
          await fetchAccounts()
        }
      } catch (err: any) {
        if (err?.status === 404) {
          if (pollTimerRef.current) {
            clearInterval(pollTimerRef.current)
            pollTimerRef.current = null
          }
          setShowCreateModal(false)
          setPendingAccount(null)
          setPollError(null)
          await fetchAccounts()
        } else {
          setPollError('网络异常，请检查连接后重试')
        }
      }
    }, POLL_INTERVAL)
  }

  useEffect(() => {
    if (!pendingAccount?.expiresAt) return
    const expiresAt = new Date(pendingAccount.expiresAt).getTime()
    const now = Date.now()
    if (expiresAt <= now) {
      setQrExpired(true)
      if (pollTimerRef.current) {
        clearInterval(pollTimerRef.current)
        pollTimerRef.current = null
      }
      return
    }
    const timeout = setTimeout(() => {
      setQrExpired(true)
      if (pollTimerRef.current) {
        clearInterval(pollTimerRef.current)
        pollTimerRef.current = null
      }
    }, expiresAt - now)
    return () => clearTimeout(timeout)
  }, [pendingAccount?.expiresAt])

  const handleCreate = async () => {
    setCreateError(null)
    setQrExpired(false)
    try {
      setCreating(true)
      const pending = await createCookieAccount()
      setPendingAccount(pending)
      if (pending.accountId) {
        startPolling(pending.accountId)
      }
    } catch (err: any) {
      if (err.status === 429) {
        setCreateError('并发登录已达上限，请稍后再试')
      } else {
        setCreateError('创建失败，请重试')
      }
    } finally {
      setCreating(false)
    }
  }

  const handleCloseCreateModal = () => {
    if (pollTimerRef.current) {
      clearInterval(pollTimerRef.current)
      pollTimerRef.current = null
    }
    setShowCreateModal(false)
    setPendingAccount(null)
    setCreateError(null)
    setPollError(null)
    setQrExpired(false)
  }

  const handleDelete = async (accountId: string) => {
    try {
      await deleteCookieAccount(accountId)
      await fetchAccounts()
    } catch {
      setDeleteError('删除失败，请重试')
    }
  }

  const formatDate = (dateString?: string | null) => {
    if (!dateString) return '-'
    return new Date(dateString).toLocaleString()
  }

  return (
    <div>
      <h2>Cookie 管理</h2>

      <div style={{ marginBottom: '1rem', display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
        <button
          onClick={() => {
            setShowCreateModal(true)
            setCreateError(null)
            handleCreate()
          }}
          style={{ padding: '0.5rem 1rem' }}
        >
          新增帐号
        </button>
      </div>

      {deleteError && (
        <p style={{ color: '#f87171', margin: '0.5rem 0' }}>{deleteError}</p>
      )}

      {loading ? (
        <p>加载中...</p>
      ) : error ? (
        <p style={{ color: '#f87171' }}>{error}</p>
      ) : accounts.length === 0 ? (
        <p>暂无帐号，点击「新增帐号」添加</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>昵称/MID</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>Session 状态</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>Cookie 状态</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>最近刷新时间</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>最近登录时间</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
            </tr>
          </thead>
          <tbody>
            {accounts.map((item) => {
              const status = item.status ?? ''
              const canDelete = status === 'active' || status === 'expired' || status === 'invalid'

              return (
                <tr key={item.accountId}>
                  <td style={{ padding: '0.5rem' }}>
                    {item.uname
                      ? `${item.uname}（${item.mid ?? '-'}）`
                      : item.mid ?? '-'}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    <span
                      style={{
                        color: SESSION_STATUS_COLORS[status] ?? '#e2e8f0',
                      }}
                    >
                      {SESSION_STATUS_LABELS[status] ?? status}
                    </span>
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {item.cookieStatus ?? '-'}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {formatDate(item.lastRefreshAt)}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {formatDate(item.lastLoginAt)}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {canDelete && (
                      <button
                        onClick={() => handleDelete(item.accountId!)}
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
              backgroundColor: '#1e293b',
              color: '#e2e8f0',
              padding: '1.5rem',
              borderRadius: '0.5rem',
              minWidth: '400px',
              textAlign: 'center',
            }}
          >
            <h3>扫码登录</h3>

            {creating ? (
              <p>正在创建登录会话...</p>
            ) : createError ? (
              <>
                <p style={{ color: '#f87171' }}>{createError}</p>
                <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'center' }}>
                  <button
                    onClick={handleCreate}
                    style={{ padding: '0.5rem 1rem' }}
                  >
                    重试
                  </button>
                  <button
                    onClick={handleCloseCreateModal}
                    style={{
                      padding: '0.5rem 1rem',
                      backgroundColor: '#475569',
                      color: '#e2e8f0',
                      border: 'none',
                      borderRadius: '0.25rem',
                      cursor: 'pointer',
                    }}
                  >
                    取消
                  </button>
                </div>
              </>
            ) : pendingAccount?.qrCodeImage ? (
              <>
                {qrExpired ? (
                  <>
                    <p style={{ color: '#f87171', marginBottom: '0.5rem' }}>
                      二维码已过期，请重新生成
                    </p>
                    {pollError && (
                      <p style={{ color: '#f87171', fontSize: '0.875rem', marginBottom: '0.5rem' }}>
                        {pollError}
                      </p>
                    )}
                    <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'center' }}>
                      <button
                        onClick={() => {
                          setQrExpired(false)
                          setPendingAccount(null)
                          handleCreate()
                        }}
                        style={{ padding: '0.5rem 1rem' }}
                      >
                        重新生成
                      </button>
                      <button
                        onClick={handleCloseCreateModal}
                        style={{
                          padding: '0.5rem 1rem',
                          backgroundColor: '#475569',
                          color: '#e2e8f0',
                          border: 'none',
                          borderRadius: '0.25rem',
                          cursor: 'pointer',
                        }}
                      >
                        取消
                      </button>
                    </div>
                  </>
                ) : (
                  <>
                    <p style={{ marginBottom: '0.5rem', fontSize: '0.875rem', color: '#94a3b8' }}>
                      请使用 B 站客户端扫描二维码
                    </p>
                    <img
                      src={`data:image/png;base64,${pendingAccount.qrCodeImage}`}
                      alt="扫码登录二维码"
                      style={{ maxWidth: '260px', marginBottom: '0.5rem' }}
                    />
                    {pendingAccount.expiresAt && (
                      <p style={{ fontSize: '0.75rem', color: '#94a3b8' }}>
                        过期时间：{formatDate(pendingAccount.expiresAt)}
                      </p>
                    )}
                    {pollError && (
                      <p style={{ fontSize: '0.75rem', color: '#f87171', marginBottom: '0.5rem' }}>
                        {pollError}
                      </p>
                    )}
                    <p style={{ fontSize: '0.75rem', color: '#eab308' }}>
                      等待扫码中...
                    </p>
                    <div style={{ marginTop: '1rem' }}>
                      <button
                        onClick={handleCloseCreateModal}
                        style={{
                          padding: '0.5rem 1rem',
                          backgroundColor: '#475569',
                          color: '#e2e8f0',
                          border: 'none',
                          borderRadius: '0.25rem',
                          cursor: 'pointer',
                        }}
                      >
                        取消
                      </button>
                    </div>
                  </>
                )}
              </>
            ) : (
              <p>未获取到二维码，请重试</p>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
