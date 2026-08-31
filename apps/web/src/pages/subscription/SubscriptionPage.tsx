import { useEffect, useState } from 'react'
import {
  listBackstageSubscriptions,
  subscribe,
  unsubscribe,
} from '../../api/client'
import type { components } from '../../api/schema'

type SubscriptionItem = components['schemas']['BackstageSubscriptionItem']

const TYPE_LABELS: Record<string, string> = {
  danmaku: '弹幕',
  dynamic: '动态',
  guard: '舰长',
}

export function SubscriptionPage() {
  const [subscriptions, setSubscriptions] = useState<SubscriptionItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [mid, setMid] = useState('')
  const [type, setType] = useState<'danmaku' | 'dynamic' | 'guard' | ''>('')
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)

  const [confirmingCancel, setConfirmingCancel] = useState<string | null>(null)

  const fetchSubscriptions = async () => {
    try {
      setLoading(true)
      setError(null)
      const data = await listBackstageSubscriptions()
      setSubscriptions(data)
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

    try {
      setSubmitting(true)
      await subscribe({ mid: midNum, type })
      setMid('')
      setType('')
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

  const handleCancel = async (mid: number, type: 'danmaku' | 'dynamic' | 'guard') => {
    try {
      await unsubscribe(mid, type)
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
              setType(e.target.value as 'danmaku' | 'dynamic' | 'guard' | '')
            }
            style={{ padding: '0.5rem' }}
          >
            <option value="">请选择类型</option>
            <option value="danmaku">弹幕</option>
            <option value="dynamic">动态</option>
            <option value="guard">舰长</option>
          </select>
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
      ) : subscriptions.length === 0 ? (
        <p>暂无订阅</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>主播昵称</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>主播 ID</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>类型</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>订阅时间</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>操作</th>
            </tr>
          </thead>
          <tbody>
            {subscriptions.map((item, index) => {
              const key = `${item.mid}-${item.type}-${index}`
              const isConfirming = confirmingCancel === key

              return (
                <tr key={key}>
                  <td style={{ padding: '0.5rem' }}>
                    {item.streamer?.name ?? '暂无资料'}
                  </td>
                  <td style={{ padding: '0.5rem' }}>{item.mid}</td>
                  <td style={{ padding: '0.5rem' }}>
                    {TYPE_LABELS[item.type ?? ''] ?? item.type}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {formatDate(item.subscribedAt)}
                  </td>
                  <td style={{ padding: '0.5rem' }}>
                    {isConfirming ? (
                      <span style={{ display: 'flex', gap: '0.5rem' }}>
                        <button
                          onClick={() =>
                            handleCancel(
                              item.mid!,
                              item.type as 'danmaku' | 'dynamic' | 'guard',
                            )
                          }
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
                        onClick={() => setConfirmingCancel(key)}
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
