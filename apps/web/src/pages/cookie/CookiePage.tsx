import { useEffect, useState } from 'react'
import { listCookies, addCookie } from '../../api/client'
import type { components } from '../../api/schema'

type CookieItem = components['schemas']['CookieItem']

const STATUS_LABELS: Record<string, string> = {
  active: '可用',
  offline: '离线',
}

const STATUS_COLORS: Record<string, string> = {
  active: '#22c55e',
  offline: '#94a3b8',
}

export function CookiePage() {
  const [cookies, setCookies] = useState<CookieItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [cookieInput, setCookieInput] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [submitSuccess, setSubmitSuccess] = useState(false)

  const fetchCookies = async () => {
    try {
      setLoading(true)
      setError(null)
      const data = await listCookies()
      setCookies(data)
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchCookies()
  }, [])

  const handleAddCookie = async (e: React.FormEvent) => {
    e.preventDefault()
    setSubmitError(null)
    setSubmitSuccess(false)

    if (!cookieInput.trim()) {
      setSubmitError('请输入 Cookie')
      return
    }

    try {
      setSubmitting(true)
      await addCookie({ cookie: cookieInput })
      setCookieInput('')
      setSubmitSuccess(true)
      await fetchCookies()
    } catch (err: any) {
      if (err.status === 400 && err.serverMessage) {
        setSubmitError(err.serverMessage)
      } else {
        setSubmitError('录入失败，请重试')
      }
    } finally {
      setSubmitting(false)
    }
  }

  const formatDate = (dateString?: string | null) => {
    if (!dateString) return '-'
    return new Date(dateString).toLocaleString()
  }

  const truncateId = (id?: string) => {
    if (!id) return '-'
    if (id.length <= 20) return id
    return `${id.slice(0, 10)}...${id.slice(-10)}`
  }

  return (
    <div>
      <h2>Cookie 管理</h2>

      <form onSubmit={handleAddCookie} style={{ marginBottom: '1rem' }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
          <textarea
            value={cookieInput}
            onChange={(e) => setCookieInput(e.target.value)}
            placeholder="粘贴 Cookie 内容"
            rows={4}
            style={{ padding: '0.5rem', resize: 'vertical' }}
          />
          <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
            <button
              type="submit"
              disabled={submitting}
              style={{ padding: '0.5rem 1rem' }}
            >
              {submitting ? '录入中...' : '录入 Cookie'}
            </button>
            {submitSuccess && (
              <span style={{ color: '#22c55e' }}>录入成功</span>
            )}
          </div>
        </div>
        {submitError && (
          <p style={{ color: '#f87171', margin: '0.5rem 0' }}>{submitError}</p>
        )}
      </form>

      {loading ? (
        <p>加载中...</p>
      ) : error ? (
        <p style={{ color: '#f87171' }}>{error}</p>
      ) : cookies.length === 0 ? (
        <p>暂无 Cookie</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>ID</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>状态</th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>
                最近校验时间
              </th>
              <th style={{ textAlign: 'left', padding: '0.5rem' }}>
                最近使用时间
              </th>
            </tr>
          </thead>
          <tbody>
            {cookies.map((item, index) => (
              <tr key={item.id ?? index}>
                <td style={{ padding: '0.5rem' }}>{truncateId(item.id)}</td>
                <td style={{ padding: '0.5rem' }}>
                  <span
                    style={{
                      color: STATUS_COLORS[item.status ?? ''] ?? '#e2e8f0',
                    }}
                  >
                    {STATUS_LABELS[item.status ?? ''] ?? item.status}
                  </span>
                </td>
                <td style={{ padding: '0.5rem' }}>
                  {formatDate(item.lastCheckedAt)}
                </td>
                <td style={{ padding: '0.5rem' }}>
                  {formatDate(item.lastUsedAt)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
