import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor, fireEvent, within } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { NotificationPage } from './NotificationPage'
import * as client from '../../api/client'

vi.mock('../../api/client')

describe('NotificationPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('should render the loading state initially', () => {
    vi.mocked(client.listNotificationBindings).mockReturnValue(new Promise(() => {}))
    render(<NotificationPage />)
    expect(screen.getByText('通知管理')).toBeInTheDocument()
    expect(document.querySelector('.ant-spin')).toBeInTheDocument()
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
    expect(screen.getByRole('button', { name: '重试' })).toBeInTheDocument()
  })

  it('should open create modal with event type select', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([])
    vi.mocked(client.listNotifyGroups).mockResolvedValue([])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('暂无通知绑定')).toBeInTheDocument()
    })

    fireEvent.click(screen.getAllByText('新增绑定')[0])

    const dialog = await screen.findByRole('dialog', { name: '新增绑定' })
    expect(within(dialog).getByText('通知类型')).toBeInTheDocument()
    expect(within(dialog).getByText('选择群')).toBeInTheDocument()
    expect(within(dialog).getByText('bilibili帐号失效通知')).toBeInTheDocument()
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

    fireEvent.click(screen.getAllByText('新增绑定')[0])

    const dialog = await screen.findByRole('dialog', { name: '新增绑定' })
    await waitFor(() => {
      expect(within(dialog).getAllByRole('combobox').length).toBeGreaterThanOrEqual(2)
    })

    const combos = within(dialog).getAllByRole('combobox')
    fireEvent.mouseDown(combos[0])
    fireEvent.click(await screen.findByTitle('微博帐号失效通知'))

    fireEvent.mouseDown(combos[1])
    fireEvent.click(await screen.findByTitle('运维群（123456）'))

    fireEvent.click(within(dialog).getByRole('button', { name: '创建' }))

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

    fireEvent.click(screen.getAllByText('新增绑定')[0])

    const dialog = await screen.findByRole('dialog', { name: '新增绑定' })
    await waitFor(() => {
      expect(within(dialog).getAllByRole('combobox').length).toBeGreaterThanOrEqual(2)
    })
    const combos = within(dialog).getAllByRole('combobox')
    fireEvent.mouseDown(combos[1])
    fireEvent.click(await screen.findByTitle('运维群（123456）'))

    fireEvent.click(within(dialog).getByRole('button', { name: '创建' }))

    await waitFor(() => {
      expect(client.createNotificationBinding).toHaveBeenCalledWith({
        eventType: 'bilibili_cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
      })
    })
  })

  it('should confirm before deleting a binding and delete it on confirm', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([
      {
        id: '1',
        eventType: 'bilibili_cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
        createdAt: '2024-01-01T00:00:00Z',
      },
    ])
    vi.mocked(client.deleteNotificationBinding).mockResolvedValue(undefined)

    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('bilibili帐号失效通知')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: '删除' }))

    await screen.findAllByText('确认删除该通知绑定？')
    fireEvent.click(screen.getByRole('button', { name: '确认删除' }))

    await waitFor(() => {
      expect(client.deleteNotificationBinding).toHaveBeenCalledWith('1')
    })
  })

  it('should not delete when the confirmation is cancelled', async () => {
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

    fireEvent.click(screen.getByRole('button', { name: '删除' }))

    await screen.findAllByText('确认删除该通知绑定？')
    fireEvent.click(screen.getByRole('button', { name: '返回' }))

    expect(client.deleteNotificationBinding).not.toHaveBeenCalled()
  })
})
