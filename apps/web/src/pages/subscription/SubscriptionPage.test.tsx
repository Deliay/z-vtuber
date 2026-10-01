import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { SubscriptionPage } from './SubscriptionPage'
import * as client from '../../api/client'

vi.mock('../../api/client')

const mockListSubscriptions = vi.mocked(client.listBackstageSubscriptions)
const mockSubscribe = vi.mocked(client.subscribe)
const mockUnsubscribe = vi.mocked(client.unsubscribe)
const mockListWeiboSubscriptions = vi.mocked(client.listWeiboSubscriptions)
const mockSubscribeWeibo = vi.mocked(client.subscribeWeibo)
const mockUnsubscribeWeibo = vi.mocked(client.unsubscribeWeibo)

/** antd Select：点开后选中文案。 */
function pickSelect(placeholder: string, optionText: string) {
  const combo = screen.getByRole('combobox', { name: placeholder }) as HTMLElement
  fireEvent.mouseDown(combo)
  return screen.findByTitle(optionText)
}

describe('SubscriptionPage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListSubscriptions.mockResolvedValue([])
    mockListWeiboSubscriptions.mockResolvedValue([])
  })

  it('should render subscription page title', async () => {
    render(<SubscriptionPage />)

    expect(screen.getByText('订阅管理')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /新增订阅/ })).toBeInTheDocument()
  })

  it('should load and display subscriptions', async () => {
    mockListSubscriptions.mockResolvedValue([
      { mid: 123, type: 'danmaku', subscribedAt: '2024-01-01T00:00:00Z' },
      { mid: 456, type: 'dynamic', subscribedAt: '2024-01-02T00:00:00Z' },
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

  it('should render streamer nickname and placeholder', async () => {
    mockListSubscriptions.mockResolvedValue([
      { mid: 123, type: 'danmaku', subscribedAt: '2024-01-01T00:00:00Z', streamer: { name: '某主播' } },
      { mid: 456, type: 'dynamic', subscribedAt: '2024-01-02T00:00:00Z', streamer: null },
      { mid: 789, type: 'dynamic', subscribedAt: '2024-01-03T00:00:00Z', streamer: { name: null } },
    ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('某主播')).toBeInTheDocument()
    })
    expect(screen.getAllByText('暂无资料').length).toBe(2)
  })

  it('should render same mid both types as separate rows with nickname', async () => {
    mockListSubscriptions.mockResolvedValue([
      { mid: 123, type: 'danmaku', subscribedAt: '2024-01-01T00:00:00Z', streamer: { name: '同主播' } },
      { mid: 123, type: 'dynamic', subscribedAt: '2024-01-02T00:00:00Z', streamer: { name: '同主播' } },
    ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getAllByText('同主播').length).toBe(2)
      expect(screen.getAllByText('123').length).toBe(2)
    })
  })

  it('should show error when loading fails', async () => {
    mockListSubscriptions.mockRejectedValue(new Error('Network error'))

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })

  it('should render guard type label and selectable option', async () => {
    mockListSubscriptions.mockResolvedValue([
      { mid: 777, type: 'guard', subscribedAt: '2024-01-05T00:00:00Z' },
    ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getAllByText('舰长').length).toBeGreaterThan(0)
      expect(screen.getByText('777')).toBeInTheDocument()
    })
  })

  it('should subscribe with guard type', async () => {
    mockSubscribe.mockResolvedValue({ mid: 888, type: 'guard', subscribedAt: '2024-01-06T00:00:00Z' })
    mockListSubscriptions
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([{ mid: 888, type: 'guard', subscribedAt: '2024-01-06T00:00:00Z' }])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })

    fireEvent.click(await pickSelect('订阅类型', '舰长'))
    fireEvent.change(screen.getByPlaceholderText('主播 ID (mid)'), { target: { value: '888' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(mockSubscribe).toHaveBeenCalledWith({ mid: 888, type: 'guard' })
      expect(screen.getByText('888')).toBeInTheDocument()
    })
  })

  it('should cancel guard subscription after modal confirmation', async () => {
    mockListSubscriptions
      .mockResolvedValueOnce([{ mid: 999, type: 'guard', subscribedAt: '2024-01-07T00:00:00Z' }])
      .mockResolvedValueOnce([])
    mockUnsubscribe.mockResolvedValue(undefined)

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('999')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    await screen.findAllByText('确认取消该订阅？')
    fireEvent.click(screen.getByRole('button', { name: '确认取消' }))

    await waitFor(() => {
      expect(mockUnsubscribe).toHaveBeenCalledWith(999, 'guard')
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })
  })

  it('should validate type selection before submission', async () => {
    render(<SubscriptionPage />)

    fireEvent.change(screen.getByPlaceholderText('主播 ID (mid)'), { target: { value: '1' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(screen.getByText('请选择订阅类型')).toBeInTheDocument()
    })
  })

  it('should validate mid input before submission', async () => {
    render(<SubscriptionPage />)

    fireEvent.click(await pickSelect('订阅类型', '弹幕'))
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(screen.getByText('请输入有效的主播 ID（纯数字）')).toBeInTheDocument()
    })
  })

  it('should reject non-numeric mid input', async () => {
    render(<SubscriptionPage />)

    fireEvent.click(await pickSelect('订阅类型', '弹幕'))
    fireEvent.change(screen.getByPlaceholderText('主播 ID (mid)'), { target: { value: '123abc' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(screen.getByText('请输入有效的主播 ID（纯数字）')).toBeInTheDocument()
    })
  })

  it('should subscribe successfully and refresh list', async () => {
    mockSubscribe.mockResolvedValue({ mid: 789, type: 'danmaku', subscribedAt: '2024-01-03T00:00:00Z' })
    mockListSubscriptions
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([{ mid: 789, type: 'danmaku', subscribedAt: '2024-01-03T00:00:00Z' }])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })

    fireEvent.click(await pickSelect('订阅类型', '弹幕'))
    fireEvent.change(screen.getByPlaceholderText('主播 ID (mid)'), { target: { value: '789' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(mockSubscribe).toHaveBeenCalledWith({ mid: 789, type: 'danmaku' })
      expect(screen.getByText('789')).toBeInTheDocument()
    })
  })

  it('should show server error on 400 response', async () => {
    const error = Object.assign(new Error('API request failed'), {
      status: 400,
      serverMessage: 'mid 非法',
    })
    mockSubscribe.mockRejectedValue(error)

    render(<SubscriptionPage />)

    fireEvent.click(await pickSelect('订阅类型', '弹幕'))
    fireEvent.change(screen.getByPlaceholderText('主播 ID (mid)'), { target: { value: '123' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(screen.getByText('mid 非法')).toBeInTheDocument()
    })
  })

  it('should show confirmation dialog when cancel is clicked', async () => {
    mockListSubscriptions.mockResolvedValue([
      { mid: 123, type: 'danmaku', subscribedAt: '2024-01-01T00:00:00Z' },
    ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('123')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    await screen.findAllByText('确认取消该订阅？')
    expect(screen.getByRole('button', { name: '确认取消' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument()
  })

  it('should handle 404 error on cancel', async () => {
    mockListSubscriptions
      .mockResolvedValueOnce([{ mid: 123, type: 'danmaku', subscribedAt: '2024-01-01T00:00:00Z' }])
      .mockResolvedValueOnce([])
    mockUnsubscribe.mockRejectedValue(Object.assign(new Error('API request failed'), { status: 404 }))

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('123')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    await screen.findAllByText('确认取消该订阅？')
    fireEvent.click(screen.getByRole('button', { name: '确认取消' }))

    await waitFor(() => {
      expect(screen.getByText('该订阅已不存在')).toBeInTheDocument()
    })
  })

  it('should display weibo subscription with platform label', async () => {
    mockListWeiboSubscriptions.mockResolvedValue([{ uid: 555, subscribedAt: '2024-01-01T00:00:00Z' }])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('555')).toBeInTheDocument()
      expect(screen.getAllByText('微博').length).toBeGreaterThan(0)
      expect(screen.getAllByText('微博动态').length).toBeGreaterThan(0)
    })
  })

  it('should subscribe a weibo account', async () => {
    mockSubscribeWeibo.mockResolvedValue({ uid: 777, subscribedAt: '2024-01-02T00:00:00Z' })
    mockListWeiboSubscriptions
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([{ uid: 777, subscribedAt: '2024-01-02T00:00:00Z' }])

    render(<SubscriptionPage />)

    fireEvent.mouseDown(screen.getByRole('combobox', { name: '订阅平台' }))
    fireEvent.click(await screen.findByTitle('微博'))

    fireEvent.change(screen.getByPlaceholderText('微博用户 ID (uid)'), { target: { value: '777' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(mockSubscribeWeibo).toHaveBeenCalledWith({ uid: 777 })
      expect(screen.getByText('777')).toBeInTheDocument()
    })
  })

  it('should reject invalid weibo uid before submission', async () => {
    render(<SubscriptionPage />)

    fireEvent.mouseDown(screen.getByRole('combobox', { name: '订阅平台' }))
    fireEvent.click(await screen.findByTitle('微博'))

    fireEvent.change(screen.getByPlaceholderText('微博用户 ID (uid)'), { target: { value: '12ab' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(screen.getByText('请输入有效的微博用户 ID（纯数字）')).toBeInTheDocument()
    })
  })

  it('should cancel a weibo subscription', async () => {
    mockListWeiboSubscriptions
      .mockResolvedValueOnce([{ uid: 333, subscribedAt: '2024-01-03T00:00:00Z' }])
      .mockResolvedValueOnce([])
    mockUnsubscribeWeibo.mockResolvedValue(undefined)

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('333')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    await screen.findAllByText('确认取消该订阅？')
    fireEvent.click(screen.getByRole('button', { name: '确认取消' }))

    await waitFor(() => {
      expect(mockUnsubscribeWeibo).toHaveBeenCalledWith(333)
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })
  })

  it('should render recording type label', async () => {
    mockListSubscriptions.mockResolvedValue([
      { mid: 321, type: 'recording', subscribedAt: '2024-01-08T00:00:00Z' },
    ])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getAllByText('录播').length).toBeGreaterThan(0)
      expect(screen.getByText('321')).toBeInTheDocument()
    })
  })

  it('should subscribe with recording type', async () => {
    mockSubscribe.mockResolvedValue({ mid: 654, type: 'recording', subscribedAt: '2024-01-09T00:00:00Z' })
    mockListSubscriptions
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([{ mid: 654, type: 'recording', subscribedAt: '2024-01-09T00:00:00Z' }])

    render(<SubscriptionPage />)

    await waitFor(() => {
      expect(screen.getByText('暂无订阅')).toBeInTheDocument()
    })

    fireEvent.click(await pickSelect('订阅类型', '录播'))
    fireEvent.change(screen.getByPlaceholderText('主播 ID (mid)'), { target: { value: '654' } })
    fireEvent.click(screen.getByRole('button', { name: /新增订阅/ }))

    await waitFor(() => {
      expect(mockSubscribe).toHaveBeenCalledWith({ mid: 654, type: 'recording' })
      expect(screen.getByText('654')).toBeInTheDocument()
    })
  })
})
