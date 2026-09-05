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

  it('should render bindings list', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([
      {
        id: '1',
        eventType: 'cookie_invalid',
        platform: 'onebot',
        guildId: 'g1',
        guildName: 'Guild 1',
        channelId: 'c1',
        channelName: 'Channel 1',
        createdAt: '2024-01-01T00:00:00Z',
      },
    ])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('Cookie 失效')).toBeInTheDocument()
      expect(screen.getByText('Guild 1')).toBeInTheDocument()
      expect(screen.getByText('Channel 1')).toBeInTheDocument()
    })
  })

  it('should render error state', async () => {
    vi.mocked(client.listNotificationBindings).mockRejectedValue(new Error('Network error'))
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('服务不可用')).toBeInTheDocument()
    })
  })

  it('should open create modal when clicking new binding button', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([])
    vi.mocked(client.listNotifyChannels).mockResolvedValue([])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('暂无通知绑定')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('新增绑定'))

    expect(screen.getByText('选择频道')).toBeInTheDocument()
  })

  it('should show delete confirmation', async () => {
    vi.mocked(client.listNotificationBindings).mockResolvedValue([
      {
        id: '1',
        eventType: 'cookie_invalid',
        platform: 'onebot',
        guildId: 'g1',
        guildName: 'Guild 1',
        channelId: 'c1',
        channelName: 'Channel 1',
        createdAt: '2024-01-01T00:00:00Z',
      },
    ])
    render(<NotificationPage />)
    await waitFor(() => {
      expect(screen.getByText('Cookie 失效')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('删除'))

    expect(screen.getByText('确认删除')).toBeInTheDocument()
    expect(screen.getByText('返回')).toBeInTheDocument()
  })
})
