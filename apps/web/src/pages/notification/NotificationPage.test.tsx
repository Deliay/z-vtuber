import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, fireEvent } from '@testing-library/react'
import { NotificationPage } from './NotificationPage'
import * as client from '../../api/client'

vi.mock('../../api/client')

describe('NotificationPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('should render loading state initially', () => {
    vi.mocked(client.listNotificationBindings).mockReturnValue(new Promise(() => {}))
    render(<NotificationPage />)
    expect(screen.getByText('加载中...')).toBeInTheDocument()
  })

  it('should render empty state when no bindings', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('暂无通知绑定')).toBeInTheDocument()
    })
  })

  it('should render bindings list with platform-specific labels', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([
      {
        id: '1',
        eventType: 'bilibili_cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
        createdAt: '2024-01-01T00:00:00Z',
      },
      {
        id: '2',
        eventType: 'weibo_cookie_invalid',
        groupId: '654321',
        groupName: '微博群',
        createdAt: '2024-01-02T00:00:00Z',
      },
    ])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('bilibili帐号失效通知')).toBeInTheDocument()
      expect(screen.getByText('微博帐号失效通知')).toBeInTheDocument()
      expect(screen.getByText('运维群（123456）')).toBeInTheDocument()
    })
  })

  it('should treat legacy cookie_invalid as bilibili label', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([
      {
        id: '1',
        eventType: 'cookie_invalid' as unknown as 'bilibili_cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
        createdAt: '2024-01-01T00:00:00Z',
      },
    ])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('bilibili帐号失效通知')).toBeInTheDocument()
    })
  })

  it('should render error state', async () => {
    vi.mocked(client.listNotificationBindings).mockRejectedValue(new Error('Network error'))
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })

  it('should open create modal with event type select', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([])
    vi.mocked(client.listNotifyGroups).mockResolvedValue([])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('暂无通知绑定')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('新增绑定'))

    expect(screen.getByText('选择群')).toBeInTheDocument()
    expect(screen.getByText('bilibili帐号失效通知')).toBeInTheDocument()
    expect(screen.getByText('微博帐号失效通知')).toBeInTheDocument()
  })

  it('should submit the selected eventType when creating a binding', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([])
    vi.mocked(client.listNotifyGroups).mockResolvedValue([
      { groupId: '123456', groupName: '运维群' },
    ])
    vi.mocked(client.createNotificationBinding).mockResolvedValue({
      id: '1',
      eventType: 'weibo_cookie_invalid',
      groupId: '123456',
      groupName: '运维群',
      createdAt: '2024-01-01T00:00:00Z',
    })

    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('暂无通知绑定')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('新增绑定'))

    await waitFor(() => {
      expect(screen.getByText('运维群（123456）')).toBeInTheDocument()
    })

    const selects = screen.getAllByRole('combobox')
    fireEvent.change(selects[0], { target: { value: 'weibo_cookie_invalid' } })
    fireEvent.change(selects[1], { target: { value: '123456' } })

    fireEvent.click(screen.getByText('创建'))

    await waitFor(() => {
      expect(client.createNotificationBinding).toHaveBeenCalledWith({
        eventType: 'weibo_cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
      })
    })
  })

  it('should default to bilibili eventType when creating a binding', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([])
    vi.mocked(client.listNotifyGroups).mockResolvedValue([
      { groupId: '123456', groupName: '运维群' },
    ])
    vi.mocked(client.createNotificationBinding).mockResolvedValue({
      id: '1',
      eventType: 'bilibili_cookie_invalid',
      groupId: '123456',
      groupName: '运维群',
      createdAt: '2024-01-01T00:00:00Z',
    })

    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('暂无通知绑定')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('新增绑定'))

    await waitFor(() => {
      expect(screen.getByText('运维群（123456）')).toBeInTheDocument()
    })

    const selects = screen.getAllByRole('combobox')
    fireEvent.change(selects[1], { target: { value: '123456' } })

    fireEvent.click(screen.getByText('创建'))

    await waitFor(() => {
      expect(client.createNotificationBinding).toHaveBeenCalledWith({
        eventType: 'bilibili_cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
      })
    })
  })

  it('should show delete confirmation', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([
      {
        id: '1',
        eventType: 'bilibili_cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
        createdAt: '2024-01-01T00:00:00Z',
      },
    ])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('bilibili帐号失效通知')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('删除'))

    expect(screen.getByText('确认删除')).toBeInTheDocument()
    expect(screen.getByText('返回')).toBeInTheDocument()
  })
})
