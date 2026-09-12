import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react'
import { CookiePage } from './CookiePage'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockListAccounts = vi.mocked(client.listCookieAccounts)
const mockCreateAccount = vi.mocked(client.createCookieAccount)
const mockGetAccount = vi.mocked(client.getCookieAccount)
const mockDeleteAccount = vi.mocked(client.deleteCookieAccount)

describe('CookiePage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListAccounts.mockResolvedValue([])
  })

  it('should render cookie page title', async () => {
    await act(async () => {
      render(<CookiePage />)
    })

    expect(screen.getByText('Cookie 管理')).toBeInTheDocument()
    expect(screen.getByText('新增帐号')).toBeInTheDocument()
  })

  it('should load and display accounts', async () => {
    mockListAccounts.mockResolvedValue([
      {
        accountId: 'acc-1',
        mid: 12345,
        uname: '用户A',
        status: 'active',
        cookieStatus: 'active',
        lastRefreshAt: '2024-01-01T00:00:00Z',
        lastLoginAt: '2024-01-01T00:00:00Z',
      },
      {
        accountId: 'acc-2',
        mid: 67890,
        uname: null,
        status: 'expired',
        cookieStatus: 'expired',
        lastRefreshAt: null,
        lastLoginAt: null,
      },
    ])

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText(/用户A/)).toBeInTheDocument()
      expect(screen.getByText(/67890/)).toBeInTheDocument()
    })
  })

  it('should show empty state when no accounts', async () => {
    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText(/暂无帐号/)).toBeInTheDocument()
    })
  })

  it('should show error when loading fails', async () => {
    mockListAccounts.mockRejectedValue(new Error('Network error'))

    await act(async () => {
      render(<CookiePage />)
    })

    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })

  it('should open create modal and start polling on click', async () => {
    const futureDate = new Date(Date.now() + 10 * 60 * 1000).toISOString()
    mockCreateAccount.mockResolvedValue({
      accountId: 'new-acc',
      qrCodeImage: 'fake-base64-qr',
      expiresAt: futureDate,
    })
    mockGetAccount.mockResolvedValue({
      accountId: 'new-acc',
      status: 'pending_login',
    })

    await act(async () => {
      render(<CookiePage />)
    })

    await act(async () => {
      fireEvent.click(screen.getByText('新增帐号'))
    })

    await waitFor(() => {
      expect(screen.getByText('扫码登录')).toBeInTheDocument()
    })

    expect(mockCreateAccount).toHaveBeenCalled()
  })

  it('should show error on create 429', async () => {
    const error = new Error('Too Many Requests')
    ;(error as any).status = 429
    mockCreateAccount.mockRejectedValue(error)

    await act(async () => {
      render(<CookiePage />)
    })

    await act(async () => {
      fireEvent.click(screen.getByText('新增帐号'))
    })

    await waitFor(() => {
      expect(screen.getByText('并发登录已达上限，请稍后再试')).toBeInTheDocument()
    })
  })

  it('should show generic error on create failure', async () => {
    mockCreateAccount.mockRejectedValue(new Error('Network error'))

    await act(async () => {
      render(<CookiePage />)
    })

    await act(async () => {
      fireEvent.click(screen.getByText('新增帐号'))
    })

    await waitFor(() => {
      expect(screen.getByText('创建失败，请重试')).toBeInTheDocument()
    })
  })

  it('should delete account and refresh list', async () => {
    mockListAccounts
      .mockResolvedValueOnce([
        {
          accountId: 'acc-1',
          mid: 12345,
          uname: '用户A',
          status: 'active',
          cookieStatus: 'active',
          lastRefreshAt: null,
          lastLoginAt: null,
        },
      ])
      .mockResolvedValueOnce([])

    mockDeleteAccount.mockResolvedValue(undefined)

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
      expect(mockDeleteAccount).toHaveBeenCalledWith('acc-1')
      expect(screen.getByText(/暂无帐号/)).toBeInTheDocument()
    })
  })

  it('should display status labels correctly', async () => {
    mockListAccounts.mockResolvedValue([
      {
        accountId: 'acc-1',
        mid: 12345,
        uname: '用户A',
        status: 'active',
        cookieStatus: 'active',
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
