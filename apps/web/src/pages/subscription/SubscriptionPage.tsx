import { useEffect, useState } from 'react'
import {
  listBackstageSubscriptions,
  listWeiboSubscriptions,
  subscribe,
  subscribeWeibo,
  unsubscribe,
  unsubscribeWeibo,
} from '../../api/client'

type Platform = 'bilibili' | 'weibo'

interface Row {
  key: string
  platform: Platform
  id: number
  type?: 'danmaku' | 'dynamic' | 'guard' | 'recording'
  subscribedAt?: string
  name?: string | null
}

const TYPE_LABELS: Record<string, string> = {
  danmaku: '弹幕',
  dynamic: '动态',
  guard: '舰长',
  recording: '录播',
}

const PLATFORM_LABELS: Record<Platform, string> = {
  bilibili: 'B站',
  weibo: '微博',
}

export function SubscriptionPage() {
  const [rows, setRows] = useState<Row[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [platform, setPlatform] = useState<Platform>('bilibili')
  const [mid, setMid] = useState('')
  const [uid, setUid] = useState('')
  const [type, setType] = useState<'danmaku' | 'dynamic' | 'guard' | 'recording' | ''>('')
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)

  const [confirmingCancel, setConfirmingCancel] = useState<string | null>(null)

  const fetchSubscriptions = async () => {
    try {
      setLoading(true)
      setError(null)
      const [biliItems, weiboItems] = await Promise.all([
        listBackstageSubscriptions(),
        listWeiboSubscriptions(),
      ])

      const biliRows: Row[] = (biliItems ?? []).map((item) => ({
        key: `bilibili-${item.mid}-${item.type}-${item.subscribedAt ?? ''}`,
        platform: 'bilibili' as const,
        id: item.mid!,
        type: (item.type ?? 'dynamic') as 'danmaku' | 'dynamic' | 'guard' | 'recording',
        subscribedAt: item.subscribedAt,
        name: item.streamer?.name,
      }))

      const weiboRows: Row[] = (weiboItems ?? []).map((item) => ({
        key: `weibo-${item.uid}`,
        platform: 'weibo' as const,
        id: item.uid!,
        subscribedAt: item.subscribedAt,
      }))

      setRows([...biliRows, ...weiboRows])
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchSubscriptions()
  }, [])

  const handleSubscribe = async (e: React.FormEvent) => {
    e.preventDefault()
    setSubmitError(null)

    try {
      setSubmitting(true)
      if (platform === 'bilibili') {
        if (!type) {
          setSubmitError('请选择订阅类型')
          return
        }

        if (!mid || !/^\d+$/.test(mid)) {
          setSubmitError('请输入有效的主播 ID（纯数字）')
          return
        }

        const midNum = Number(mid)
        if (midNum <= 0) {
          setSubmitError('主播 ID 必须为正整数')
          return
        }

        await subscribe({ mid: midNum, type })
        setMid('')
        setType('')
      } else {
        if (!uid || !/^\d+$/.test(uid)) {
          setSubmitError('请输入有效的微博用户 ID（纯数字）')
          return
        }

        const uidNum = Number(uid)
        if (uidNum <= 0) {
          setSubmitError('微博用户 ID 必须为正整数')
          return
        }

        await subscribeWeibo({ uid: uidNum })
        setUid('')
      }
      await fetchSubscriptions()
    } catch (err: any) {
      if (err.status === 400 && err.serverMessage) {
        setSubmitError(err.serverMessage)
      } else {
        setSubmitError('提交失败，请重试')
      }
    } finally {
      setSubmitting(false)
    }
  }

  const handleCancel = async (row: Row) => {
    try {
      if (row.platform === 'weibo') {
        await unsubscribeWeibo(row.id)
      } else if (row.type) {
        await unsubscribe(row.id, row.type)
      }
      await fetchSubscriptions()
    } catch (err: any) {
      if (err.status === 404) {
        setSubmitError('该订阅已不存在')
        await fetchSubscriptions()
      } else {
        setSubmitError('取消订阅失败，请重试')
      }
    }
    setConfirmingCancel(null)
  }

  const formatDate = (dateString?: string | null) => {
    if (!dateString) return '-'
    return new Date(dateString).toLocaleString()
  }

  return (
    <div>
      <h2>订阅管理</h2>

      <form onSubmit={handleSubscribe} style={{ marginBottom: '1rem' }}>
        <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
          <select
            value={platform}
            onChange={(e) => setPlatform(e.target.value as Platform)}
            style={{ padding: '0.5rem' }}
            aria-label="订阅平台"
          >
            <option value="bilibili">B站</option>
            <option value="weibo">微博</option>
          </select>

          {platform === 'bilibili' ? (
            <>
              <input
                type="text"
                value={mid}
                onChange={(e) => setMid(e.target.value)}
                placeholder="主播 ID (mid)"
                style={{ padding: '0.5rem' }}
              />
              <select
                value={type}
                onChange={(e) =>
                  setType(e.target.value as 'danmaku' | 'dynamic' | 'guard' | 'recording' | '')
                }
                style={{ padding: '0.5rem' }}
              >
                <option value="">请选择类型</option>
                <option value="danmaku">弹幕</option>
                <option value="dynamic">动态</option>
                <option value="guard">舰长</option>
                <option value="recording">录播</option>
              </select>
            </>
          ) : (
            <input
              type="text"
              value={uid}
              onChange={(e) => setUid(e.target.value)}
              placeholder="微博用户 ID (uid)"
              style={{ padding: '0.5rem' }}
            />
          )}

          <button
            type="submit"
            disabled={submitting}
            style={{ padding: '0.5rem 1rem' }}
          >
            {submitting ? '提交中...' : '新增订阅'}
          </button>
        </div>
        {submitError && (
          <p style={{ color: '#f87171', margin: '0.5rem 0' }}>{submitError}</p>
        )}
      </form>

      {loading ? (
        <p>加载中...</p>
      ) : error ? (
        <p style={{ color: '#f87171' }}>{error}</p>
      ) : rows.length === 0 ? (
        <p>暂无订阅</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>平台</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>主播昵称</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>主播 ID</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>类型</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>订阅时间</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const isConfirming = confirmingCancel === row.key

              return (
                <tr key={row.key}>
                  <td style={{ padding: '0.5rem' }}>
                    {PLATFORM_LABELS[row.platform]}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {row.platform === 'bilibili'
                      ? (row.name ?? '暂无资料')
                      : '-'}
                  </td>
                  <td style={{ padding: '0.5rem' }}>{row.id}</td>
                  <td style={{ padding: '0.5rem' }}>
                    {row.platform === 'weibo'
                      ? '微博动态'
                      : (TYPE_LABELS[row.type ?? ''] ?? row.type)}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {formatDate(row.subscribedAt)}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {isConfirming ? (
                      <span style={{ display: 'flex', gap: '0.5rem' }}>
                        <button
                          onClick={() => handleCancel(row)}
                          style={{ padding: '0.25rem 0.5rem' }}
                        >
                          确认取消
                        </button>
                        <button
                          onClick={() => setConfirmingCancel(null)}
                          style={{ padding: '0.25rem 0.5rem' }}
                        >
                          返回
                        </button>
                      </span>
                    ) : (
                      <button
                        onClick={() => setConfirmingCancel(row.key)}
                        style={{ padding: '0.25rem 0.5rem' }}
                      >
                        取消
                      </button>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </div>
  )
}
