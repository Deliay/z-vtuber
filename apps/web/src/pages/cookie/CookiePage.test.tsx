import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { CookiePage } from './CookiePage'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockListCookies = vi.mocked(client.listBackstageCookies)
const mockAddCookie = vi.mocked(client.addCookie)

describe('CookiePage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListCookies.mockResolvedValue([])
  })

  it('should render cookie page title', async () => {
    render(<CookiePage />)

    expect(screen.getByText('Cookie 管理')).toBeInTheDocument()
    expect(screen.getByText('录入 Cookie')).toBeInTheDocument()
  })

  it('should load and display cookies', async () => {
    mockListCookies.mockResolvedValue([
      {
        id: 'cookie-1',
        status: 'active',
        lastCheckedAt: '2024-01-01T00:00:00Z',
        lastUsedAt: '2024-01-01T00:00:00Z',
      },
      {
        id: 'cookie-2',
        status: 'offline',
        lastCheckedAt: null,
        lastUsedAt: null,
      },
    ])

    render(<CookiePage />)

    await waitFor(() => {
      expect(screen.getByText('cookie-1')).toBeInTheDocument()
      expect(screen.getByText('cookie-2')).toBeInTheDocument()
      expect(screen.getByText('可用')).toBeInTheDocument()
      expect(screen.getByText('离线')).toBeInTheDocument()
    })
  })

  it('should show empty state when no cookies', async () => {
    render(<CookiePage />)

    await waitFor(() => {
      expect(screen.getByText('暂无 Cookie')).toBeInTheDocument()
    })
  })

  it('should render account nickname and placeholder regardless of status', async () => {
    mockListCookies.mockResolvedValue([
      {
        id: 'cookie-1',
        status: 'active',
        lastCheckedAt: null,
        lastUsedAt: null,
        account: { name: '某帐号' },
      },
      {
        id: 'cookie-2',
        status: 'offline',
        lastCheckedAt: null,
        lastUsedAt: null,
        account: null,
      },
    ])

    render(<CookiePage />)

    await waitFor(() => {
      expect(screen.getByText('某帐号')).toBeInTheDocument()
      expect(screen.getByText('暂无资料')).toBeInTheDocument()
      expect(screen.getByText('可用')).toBeInTheDocument()
      expect(screen.getByText('离线')).toBeInTheDocument()
    })
  })

  it('should show error when loading fails', async () => {
    mockListCookies.mockRejectedValue(new Error('Network error'))

    render(<CookiePage />)

    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })

  it('should validate empty cookie input', async () => {
    render(<CookiePage />)

    const submitButton = screen.getByText('录入 Cookie')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(screen.getByText('请输入 Cookie')).toBeInTheDocument()
    })
  })

  it('should validate whitespace-only cookie input', async () => {
    render(<CookiePage />)

    const textarea = screen.getByPlaceholderText('粘贴 Cookie 内容')
    fireEvent.change(textarea, { target: { value: '   ' } })

    const submitButton = screen.getByText('录入 Cookie')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(screen.getByText('请输入 Cookie')).toBeInTheDocument()
    })
  })

  it('should add cookie successfully and refresh list', async () => {
    mockAddCookie.mockResolvedValue({
      id: 'new-cookie',
      status: 'active',
      lastCheckedAt: null,
      lastUsedAt: null,
    })
    mockListCookies
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        {
          id: 'new-cookie',
          status: 'active',
          lastCheckedAt: null,
          lastUsedAt: null,
        },
      ])

    render(<CookiePage />)

    await waitFor(() => {
      expect(screen.getByText('暂无 Cookie')).toBeInTheDocument()
    })

    const textarea = screen.getByPlaceholderText('粘贴 Cookie 内容')
    fireEvent.change(textarea, { target: { value: 'test-cookie-value' } })

    const submitButton = screen.getByText('录入 Cookie')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(mockAddCookie).toHaveBeenCalledWith({ cookie: 'test-cookie-value' })
      expect(screen.getByText('new-cookie')).toBeInTheDocument()
      expect(screen.getByText('录入成功')).toBeInTheDocument()
    })
  })

  it('should show server error on 400 response', async () => {
    const error = new Error('API request failed')
    ;(error as any).status = 400
    ;(error as any).serverMessage = 'Cookie 为空'
    mockAddCookie.mockRejectedValue(error)

    render(<CookiePage />)

    const textarea = screen.getByPlaceholderText('粘贴 Cookie 内容')
    fireEvent.change(textarea, { target: { value: '' } })

    const submitButton = screen.getByText('录入 Cookie')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(screen.getByText('请输入 Cookie')).toBeInTheDocument()
    })
  })

  it('should truncate long cookie ids', async () => {
    const longId = 'a'.repeat(40)
    mockListCookies.mockResolvedValue([
      {
        id: longId,
        status: 'active',
        lastCheckedAt: null,
        lastUsedAt: null,
      },
    ])

    render(<CookiePage />)

    await waitFor(() => {
      expect(screen.getByText('aaaaaaaaaa...aaaaaaaaaa')).toBeInTheDocument()
    })
  })

  it('should display dash for null dates', async () => {
    mockListCookies.mockResolvedValue([
      {
        id: 'cookie-1',
        status: 'active',
        lastCheckedAt: null,
        lastUsedAt: null,
      },
    ])

    render(<CookiePage />)

    await waitFor(() => {
      const dashes = screen.getAllByText('-')
      expect(dashes.length).toBe(2)
    })
  })
})
