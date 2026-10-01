import { useEffect, useRef, useState } from 'react'
import {
  Alert,
  App as AntdApp,
  Button,
  Empty,
  Image,
  Modal,
  Space,
  Spin,
  Table,
  Tag,
  Typography,
} from 'antd'
import { PlusOutlined } from '@ant-design/icons'
import type { ColumnsType } from 'antd/es/table'
import {
  listCookieAccounts,
  createCookieAccount,
  getCookieAccount,
  deleteCookieAccount,
  listWeiboCookieAccounts,
  createWeiboCookieAccount,
  getWeiboCookieAccount,
  deleteWeiboCookieAccount,
} from '../../api/client'
import type { components } from '../../api/schema-cookie'
import { PageHeader } from '../../components/PageHeader'

type AccountItem = components['schemas']['AccountItem']
type PendingAccount = components['schemas']['PendingAccount']
type Platform = 'bilibili' | 'weibo'

const PLATFORM_LABELS: Record<string, string> = {
  bilibili: 'Bilibili',
  weibo: '微博',
}

const PLATFORM_COLORS: Record<string, string> = {
  bilibili: 'blue',
  weibo: 'orange',
}

const QR_HINTS: Record<Platform, string> = {
  bilibili: '请使用 B 站客户端扫描二维码',
  weibo: '请使用微博 App 扫描二维码',
}

const SESSION_STATUS_LABELS: Record<string, string> = {
  pending_login: '等待扫码',
  active: '活跃',
  expired: '已过期',
  invalid: '已失效',
}

const SESSION_STATUS_COLORS: Record<string, string> = {
  pending_login: 'gold',
  active: 'green',
  expired: 'default',
  invalid: 'red',
}

const POLL_INTERVAL = 2000

function formatDate(dateString?: string | null) {
  if (!dateString) return '-'
  return new Date(dateString).toLocaleString()
}

export function CookiePage() {
  const { message, modal } = AntdApp.useApp()
  const [accounts, setAccounts] = useState<AccountItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [showCreateModal, setShowCreateModal] = useState(false)
  const [createPlatform, setCreatePlatform] = useState<Platform>('bilibili')
  const [pendingAccount, setPendingAccount] = useState<PendingAccount | null>(null)
  const [creating, setCreating] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [pollError, setPollError] = useState<string | null>(null)
  const [qrExpired, setQrExpired] = useState(false)

  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const fetchAccounts = async () => {
    setLoading(true)
    setError(null)
    const [bilibiliResult, weiboResult] = await Promise.allSettled([
      listCookieAccounts(),
      listWeiboCookieAccounts(),
    ])

    const merged: AccountItem[] = []
    if (bilibiliResult.status === 'fulfilled') {
      merged.push(...(bilibiliResult.value ?? []))
    }
    if (weiboResult.status === 'fulfilled') {
      merged.push(...(weiboResult.value ?? []))
    }
    setAccounts(merged)

    if (bilibiliResult.status === 'rejected' && weiboResult.status === 'rejected') {
      setError('服务不可用')
    }
    setLoading(false)
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

  const stopPolling = () => {
    if (pollTimerRef.current) {
      clearInterval(pollTimerRef.current)
      pollTimerRef.current = null
    }
  }

  const startPolling = (accountId: string, platform: Platform) => {
    stopPolling()
    setPollError(null)
    pollTimerRef.current = setInterval(async () => {
      try {
        const status =
          platform === 'weibo'
            ? await getWeiboCookieAccount(accountId)
            : await getCookieAccount(accountId)
        if (status.status === 'active' || status.status === 'expired' || status.status === 'invalid') {
          stopPolling()
          setShowCreateModal(false)
          setPendingAccount(null)
          setPollError(null)
          await fetchAccounts()
        }
      } catch (err: any) {
        if (err?.status === 404) {
          stopPolling()
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
      stopPolling()
      return
    }
    const timeout = setTimeout(() => {
      setQrExpired(true)
      stopPolling()
    }, expiresAt - now)
    return () => clearTimeout(timeout)
  }, [pendingAccount?.expiresAt])

  const handleCreate = async (platform: Platform) => {
    setCreateError(null)
    setQrExpired(false)
    try {
      setCreating(true)
      const pending =
        platform === 'weibo' ? await createWeiboCookieAccount() : await createCookieAccount()
      setPendingAccount(pending)
      if (pending.accountId) {
        startPolling(pending.accountId, platform)
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
    stopPolling()
    setShowCreateModal(false)
    setPendingAccount(null)
    setCreateError(null)
    setPollError(null)
    setQrExpired(false)
  }

  const handleDelete = (account: AccountItem) => {
    if (!account.accountId) return
    modal.confirm({
      title: '确认删除该帐号？',
      content: '删除后该帐号的登录会话与 Cookie 将被清除，需重新扫码登录。',
      okText: '确认删除',
      cancelText: '返回',
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          if (account.platform === 'weibo') {
            await deleteWeiboCookieAccount(account.accountId!)
          } else {
            await deleteCookieAccount(account.accountId!)
          }
          message.success('帐号已删除')
          await fetchAccounts()
        } catch {
          message.error('删除失败，请重试')
        }
      },
    })
  }

  const handleOpenCreate = (platform: Platform) => {
    setCreatePlatform(platform)
    setShowCreateModal(true)
    handleCreate(platform)
  }

  const columns: ColumnsType<AccountItem> = [
    {
      title: '昵称/MID',
      key: 'uname',
      render: (_value, item) =>
        item.uname ? `${item.uname}（${item.mid ?? '-'}）` : (item.mid ?? '-'),
    },
    {
      title: '平台',
      dataIndex: 'platform',
      width: 110,
      render: (value: string) => (
        <Tag color={PLATFORM_COLORS[value] ?? 'default'}>
          {PLATFORM_LABELS[value ?? 'bilibili'] ?? value}
        </Tag>
      ),
    },
    {
      title: 'Session 状态',
      dataIndex: 'status',
      width: 120,
      render: (value: string) => (
        <Tag color={SESSION_STATUS_COLORS[value] ?? 'default'}>
          {SESSION_STATUS_LABELS[value] ?? value}
        </Tag>
      ),
    },
    { title: 'Cookie 状态', dataIndex: 'cookieStatus', width: 120, render: (v) => v ?? '-' },
    {
      title: '最近刷新时间',
      dataIndex: 'lastRefreshAt',
      width: 200,
      render: (value: string) => formatDate(value),
    },
    {
      title: '最近登录时间',
      dataIndex: 'lastLoginAt',
      width: 200,
      render: (value: string) => formatDate(value),
    },
    {
      title: '操作',
      key: 'action',
      width: 100,
      render: (_value, item) => {
        const status = item.status ?? ''
        const canDelete = status === 'active' || status === 'expired' || status === 'invalid'
        return canDelete ? (
          <Button danger onClick={() => handleDelete(item)}>
            删除
          </Button>
        ) : null
      },
    },
  ]

  return (
    <div>
      <PageHeader
        title="Cookie 管理"
        description="通过扫码登录管理各平台帐号与 Cookie 状态。"
        extra={
          <Space>
            <Button icon={<PlusOutlined />} onClick={() => handleOpenCreate('weibo')}>
              新增微博帐号
            </Button>
            <Button type="primary" icon={<PlusOutlined />} onClick={() => handleOpenCreate('bilibili')}>
              新增Bilibili帐号
            </Button>
          </Space>
        }
      />

      {error ? (
        <Alert
          type="error"
          showIcon
          title={error}
          action={<Button onClick={fetchAccounts}>重试</Button>}
        />
      ) : (
        <Table<AccountItem>
          rowKey={(item) => item.accountId ?? ''}
          columns={columns}
          dataSource={accounts}
          loading={loading}
          pagination={false}
          locale={{
            emptyText: <Empty description="暂无帐号，点击「新增微博帐号」或「新增Bilibili帐号」添加" />,
          }}
        />
      )}

      <Modal
        title="扫码登录"
        open={showCreateModal}
        onCancel={handleCloseCreateModal}
        footer={null}
        width={420}
        mask={{ closable: false }}
      >
        <div style={{ textAlign: 'center' }}>
          {creating ? (
            <Space orientation="vertical" align="center">
              <Spin />
              <Typography.Text type="secondary">正在创建登录会话...</Typography.Text>
            </Space>
          ) : createError ? (
            <Space orientation="vertical" align="center">
              <Typography.Text type="danger">{createError}</Typography.Text>
              <Space>
                <Button type="primary" onClick={() => handleCreate(createPlatform)}>
                  重试
                </Button>
                <Button onClick={handleCloseCreateModal}>取消</Button>
              </Space>
            </Space>
          ) : pendingAccount?.qrCodeImage ? (
            qrExpired ? (
              <Space orientation="vertical" align="center">
                <Typography.Text type="danger">二维码已过期，请重新生成</Typography.Text>
                {pollError && <Typography.Text type="danger">{pollError}</Typography.Text>}
                <Space>
                  <Button
                    type="primary"
                    onClick={() => {
                      setQrExpired(false)
                      setPendingAccount(null)
                      handleCreate(createPlatform)
                    }}
                  >
                    重新生成
                  </Button>
                  <Button onClick={handleCloseCreateModal}>取消</Button>
                </Space>
              </Space>
            ) : (
              <Space orientation="vertical" align="center">
                <Typography.Text type="secondary">{QR_HINTS[createPlatform]}</Typography.Text>
                <Image
                  src={`data:image/png;base64,${pendingAccount.qrCodeImage}`}
                  alt="扫码登录二维码"
                  width={240}
                  preview={false}
                />
                {pendingAccount.expiresAt && (
                  <Typography.Text type="secondary">
                    过期时间：{formatDate(pendingAccount.expiresAt)}
                  </Typography.Text>
                )}
                {pollError && <Typography.Text type="danger">{pollError}</Typography.Text>}
                <Tag color="gold">等待扫码中...</Tag>
                <Button onClick={handleCloseCreateModal}>取消</Button>
              </Space>
            )
          ) : (
            <Typography.Text>未获取到二维码，请重试</Typography.Text>
          )}
        </div>
      </Modal>
    </div>
  )
}
