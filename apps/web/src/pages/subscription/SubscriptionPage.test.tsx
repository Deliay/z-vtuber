import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { SubscriptionPage } from './SubscriptionPage'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockListSubscriptions = vi.mocked(client.listSubscriptions)
const mockSubscribe = vi.mocked(client.subscribe)
const mockUnsubscribe = vi.mocked(client.unsubscribe)

describe('SubscriptionPage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListSubscriptions.mockResolvedValue([])
  })

  it('should render subscription page title', async () => {
    render(<SubscriptionPage />)

    expect(screen.getByText('订阅管理')).toBeInTheDocument()
    expect(screen.getByText('新增订阅')).toBeInTheDocument()
  })

  it('should load and display subscriptions', async () => {
    mockListSubscriptions.mockResolvedValue([
      {
        mid: 123,
        type: 'danmaku',
        subscribedAt: '2024-01-01T00:00:00Z',
      },
      {
        mid: 456,
        type: 'dynamic',
        subscribedAt: '2024-01-02T00:00:00Z',
      },
    ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('123')).toBeInTheDocument()
      expect(screen.getByText('456')).toBeInTheDocument()
      expect(screen.getAllByText('弹幕').length).toBeGreaterThan(0)
      expect(screen.getAllByText('动态').length).toBeGreaterThan(0)
    })
  })

  it('should show empty state when no subscriptions', async () => {
    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })
  })

  it('should show error when loading fails', async () => {
    mockListSubscriptions.mockRejectedValue(new Error('Network error'))

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })

  it('should validate type selection before submission', async () => {
    render(<SubscriptionPage />)

    const submitButton = screen.getByText('新增订阅')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(screen.getByText('请选择订阅类型')).toBeInTheDocument()
    })
  })

  it('should validate mid input before submission', async () => {
    render(<SubscriptionPage />)

    const typeSelect = screen.getByDisplayValue('请选择类型')
    fireEvent.change(typeSelect, { target: { value: 'danmaku' } })

    const submitButton = screen.getByText('新增订阅')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(screen.getByText('请输入有效的主播 ID（纯数字）')).toBeInTheDocument()
    })
  })

  it('should reject non-numeric mid input', async () => {
    render(<SubscriptionPage />)

    const typeSelect = screen.getByDisplayValue('请选择类型')
    fireEvent.change(typeSelect, { target: { value: 'danmaku' } })

    const midInput = screen.getByPlaceholderText('主播 ID (mid)')
    fireEvent.change(midInput, { target: { value: '123abc' } })

    const submitButton = screen.getByText('新增订阅')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(screen.getByText('请输入有效的主播 ID（纯数字）')).toBeInTheDocument()
    })
  })

  it('should subscribe successfully and refresh list', async () => {
    mockSubscribe.mockResolvedValue({
      mid: 789,
      type: 'danmaku',
      subscribedAt: '2024-01-03T00:00:00Z',
    })
    mockListSubscriptions
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        {
          mid: 789,
          type: 'danmaku',
          subscribedAt: '2024-01-03T00:00:00Z',
        },
      ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })

    const typeSelect = screen.getByDisplayValue('请选择类型')
    fireEvent.change(typeSelect, { target: { value: 'danmaku' } })

    const midInput = screen.getByPlaceholderText('主播 ID (mid)')
    fireEvent.change(midInput, { target: { value: '789' } })

    const submitButton = screen.getByText('新增订阅')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(mockSubscribe).toHaveBeenCalledWith({ mid: 789, type: 'danmaku' })
      expect(screen.getByText('789')).toBeInTheDocument()
    })
  })

  it('should show server error on 400 response', async () => {
    const error = new Error('API request failed')
    ;(error as any).status = 400
    ;(error as any).serverMessage = 'mid 非法'
    mockSubscribe.mockRejectedValue(error)

    render(<SubscriptionPage />)

    const typeSelect = screen.getByDisplayValue('请选择类型')
    fireEvent.change(typeSelect, { target: { value: 'danmaku' } })

    const midInput = screen.getByPlaceholderText('主播 ID (mid)')
    fireEvent.change(midInput, { target: { value: '123' } })

    const submitButton = screen.getByText('新增订阅')
    fireEvent.click(submitButton)

    await waitFor(() => {
      expect(screen.getByText('mid 非法')).toBeInTheDocument()
    })
  })

  it('should show confirmation dialog when cancel is clicked', async () => {
    mockListSubscriptions.mockResolvedValue([
      {
        mid: 123,
        type: 'danmaku',
        subscribedAt: '2024-01-01T00:00:00Z',
      },
    ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('123')).toBeInTheDocument()
    })

    const cancelButton = screen.getByText('取消')
    fireEvent.click(cancelButton)

    expect(screen.getByText('确认取消')).toBeInTheDocument()
    expect(screen.getByText('返回')).toBeInTheDocument()
  })

  it('should cancel subscription after confirmation', async () => {
    mockListSubscriptions
      .mockResolvedValueOnce([
        {
          mid: 123,
          type: 'danmaku',
          subscribedAt: '2024-01-01T00:00:00Z',
        },
      ])
      .mockResolvedValueOnce([])
    mockUnsubscribe.mockResolvedValue(undefined)

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('123')).toBeInTheDocument()
    })

    const cancelButton = screen.getByText('取消')
    fireEvent.click(cancelButton)

    const confirmButton = screen.getByText('确认取消')
    fireEvent.click(confirmButton)

    await waitFor(() => {
      expect(mockUnsubscribe).toHaveBeenCalledWith(123, 'danmaku')
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })
  })

  it('should handle 404 error on cancel', async () => {
    mockListSubscriptions
      .mockResolvedValueOnce([
        {
          mid: 123,
          type: 'danmaku',
          subscribedAt: '2024-01-01T00:00:00Z',
        },
      ])
      .mockResolvedValueOnce([])
    const error = new Error('API request failed')
    ;(error as any).status = 404
    mockUnsubscribe.mockRejectedValue(error)

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('123')).toBeInTheDocument()
    })

    const cancelButton = screen.getByText('取消')
    fireEvent.click(cancelButton)

    const confirmButton = screen.getByText('确认取消')
    fireEvent.click(confirmButton)

    await waitFor(() => {
      expect(screen.getByText('该订阅已不存在')).toBeInTheDocument()
    })
  })
})
