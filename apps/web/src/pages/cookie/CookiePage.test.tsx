import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react'
import { CookiePage } from './CookiePage'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockListBilibili = vi.mocked(client.listCookieAccounts)
const mockListWeibo = vi.mocked(client.listWeiboCookieAccounts)
const mockCreateBilibili = vi.mocked(client.createCookieAccount)
const mockCreateWeibo = vi.mocked(client.createWeiboCookieAccount)
const mockGetBilibili = vi.mocked(client.getCookieAccount)
const mockGetWeibo = vi.mocked(client.getWeiboCookieAccount)
const mockDeleteBilibili = vi.mocked(client.deleteCookieAccount)
const mockDeleteWeibo = vi.mocked(client.deleteWeiboCookieAccount)

describe('CookiePage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListBilibili.mockResolvedValue([])
    mockListWeibo.mockResolvedValue([])
  })

  it('should render cookie page title and both create buttons', async () => {
    await act(async () => {
      render(<CookiePage />)
    })

    expect(screen.getByText('Cookie 管理')).toBeInTheDocument()
    expect(screen.getByText('新增微博帐号')).toBeInTheDocument()
    expect(screen.getByText('新增Bilibili帐号')).toBeInTheDocument()
  })

  it('should merge and display bilibili and weibo accounts with platform column', async () => {
    mockListBilibili.mockResolvedValue([
      {
        accountId: 'acc-1',
        mid: 12345,
        uname: '用户A',
        status: 'active',
        cookieStatus: 'active',
        platform: 'bilibili',
        lastRefreshAt: '2024-01-01T00:00:00Z',
        lastLoginAt: '2024-01-01T00:00:00Z',
      },
    ])
    mockListWeibo.mockResolvedValue([
      {
        accountId: 'w-1',
        mid: 999,
        uname: '微博用户',
        status: 'active',
        cookieStatus: 'active',
        platform: 'weibo',
        lastRefreshAt: null,
        lastLoginAt: null,
      },
    ])

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText(/用户A/)).toBeInTheDocument()
      expect(screen.getByText(/微博用户/)).toBeInTheDocument()
    })

    expect(screen.getByText('Bilibili')).toBeInTheDocument()
    expect(screen.getByText('微博')).toBeInTheDocument()
  })

  it('should show empty state when no accounts', async () => {
    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText(/暂无帐号/)).toBeInTheDocument()
    })
  })

  it('should show service unavailable when both lists fail', async () => {
    mockListBilibili.mockRejectedValue(new Error('Network error'))
    mockListWeibo.mockRejectedValue(new Error('Network error'))

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })

  it('should still show weibo accounts when bilibili list fails', async () => {
    mockListBilibili.mockRejectedValue(new Error('Network error'))
    mockListWeibo.mockResolvedValue([
      {
        accountId: 'w-1',
        mid: 999,
        uname: '微博用户',
        status: 'active',
        cookieStatus: 'active',
        platform: 'weibo',
        lastRefreshAt: null,
        lastLoginAt: null,
      },
    ])

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText(/微博用户/)).toBeInTheDocument()
    })
    expect(screen.queryByText('服务不可用')).not.toBeInTheDocument()
  })

  it('should create weibo account and show weibo modal hint', async () => {
    const futureDate = new Date(Date.now() + 10 * 60 * 1000).toISOString()
    mockCreateWeibo.mockResolvedValue({
      accountId: 'w-new',
      qrCodeImage: 'fake-base64-qr',
      expiresAt: futureDate,
    })
    mockGetWeibo.mockResolvedValue({
      accountId: 'w-new',
      status: 'pending_login',
    })

    await act(async () => {
      render(<CookiePage />)
    })

    await act(async () => {
      fireEvent.click(screen.getByText('新增微博帐号'))
    })

    await waitFor(() => {
      expect(screen.getByText('请使用微博 App 扫描二维码')).toBeInTheDocument()
    })

    expect(mockCreateWeibo).toHaveBeenCalled()
    expect(mockCreateBilibili).not.toHaveBeenCalled()
  })

  it('should create bilibili account and show bilibili modal hint', async () => {
    const futureDate = new Date(Date.now() + 10 * 60 * 1000).toISOString()
    mockCreateBilibili.mockResolvedValue({
      accountId: 'b-new',
      qrCodeImage: 'fake-base64-qr',
      expiresAt: futureDate,
    })
    mockGetBilibili.mockResolvedValue({
      accountId: 'b-new',
      status: 'pending_login',
    })

    await act(async () => {
      render(<CookiePage />)
    })

    await act(async () => {
      fireEvent.click(screen.getByText('新增Bilibili帐号'))
    })

    await waitFor(() => {
      expect(screen.getByText('请使用 B 站客户端扫描二维码')).toBeInTheDocument()
    })

    expect(mockCreateBilibili).toHaveBeenCalled()
    expect(mockCreateWeibo).not.toHaveBeenCalled()
  })

  it('should show error on create 429', async () => {
    const error = new Error('Too Many Requests')
    ;(error as any).status = 429
    mockCreateWeibo.mockRejectedValue(error)

    await act(async () => {
      render(<CookiePage />)
    })

    await act(async () => {
      fireEvent.click(screen.getByText('新增微博帐号'))
    })

    await waitFor(() => {
      expect(screen.getByText('并发登录已达上限，请稍后再试')).toBeInTheDocument()
    })
  })

  it('should show generic error on create failure', async () => {
    mockCreateWeibo.mockRejectedValue(new Error('Network error'))

    await act(async () => {
      render(<CookiePage />)
    })

    await act(async () => {
      fireEvent.click(screen.getByText('新增微博帐号'))
    })

    await waitFor(() => {
      expect(screen.getByText('创建失败，请重试')).toBeInTheDocument()
    })
  })

  it('should delete bilibili account via bilibili delete function', async () => {
    mockListBilibili
      .mockResolvedValueOnce([
        {
          accountId: 'acc-1',
          mid: 12345,
          uname: '用户A',
          status: 'active',
          cookieStatus: 'active',
          platform: 'bilibili',
          lastRefreshAt: null,
          lastLoginAt: null,
        },
      ])
      .mockResolvedValueOnce([])
    mockDeleteBilibili.mockResolvedValue(undefined)

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText(/用户A/)).toBeInTheDocument()
    })

    await act(async () => {
      fireEvent.click(screen.getByText('删除'))
    })

    await waitFor(() => {
      expect(mockDeleteBilibili).toHaveBeenCalledWith('acc-1')
      expect(mockDeleteWeibo).not.toHaveBeenCalled()
      expect(screen.getByText(/暂无帐号/)).toBeInTheDocument()
    })
  })

  it('should delete weibo account via weibo delete function', async () => {
    mockListWeibo
      .mockResolvedValueOnce([
        {
          accountId: 'w-1',
          mid: 999,
          uname: '微博用户',
          status: 'active',
          cookieStatus: 'active',
          platform: 'weibo',
          lastRefreshAt: null,
          lastLoginAt: null,
        },
      ])
      .mockResolvedValueOnce([])
    mockDeleteWeibo.mockResolvedValue(undefined)

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText(/微博用户/)).toBeInTheDocument()
    })

    await act(async () => {
      fireEvent.click(screen.getByText('删除'))
    })

    await waitFor(() => {
      expect(mockDeleteWeibo).toHaveBeenCalledWith('w-1')
      expect(mockDeleteBilibili).not.toHaveBeenCalled()
    })
  })

  it('should display status labels correctly', async () => {
    mockListBilibili.mockResolvedValue([
      {
        accountId: 'acc-1',
        mid: 12345,
        uname: '用户A',
        status: 'active',
        cookieStatus: 'active',
        platform: 'bilibili',
        lastRefreshAt: null,
        lastLoginAt: null,
      },
    ])

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText('活跃')).toBeInTheDocument()
    })
  })
})
