import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import App from './App'

vi.mock('./pages/subscription/SubscriptionPage', () => ({
  SubscriptionPage: () => <div data-testid="subscription-page">订阅管理页面</div>,
}))

vi.mock('./pages/cookie/CookiePage', () => ({
  CookiePage: () => <div data-testid="cookie-page">Cookie 管理页面</div>,
}))

vi.mock('./pages/notification/NotificationPage', () => ({
  NotificationPage: () => <div data-testid="notification-page">通知管理页面</div>,
}))

vi.mock('./pages/training/DatasetsPage', () => ({
  DatasetsPage: () => <div data-testid="training-datasets-page">数据集页面</div>,
}))

describe('App', () => {
  it('should render sidebar with menu items', () => {
    render(<App />)

    expect(screen.getByText('Z.Vtuber 管理后台')).toBeInTheDocument()
    expect(screen.getByText('订阅管理')).toBeInTheDocument()
    expect(screen.getByText('Cookie 管理')).toBeInTheDocument()
    expect(screen.getByText('通知')).toBeInTheDocument()
  })

  it('should render the 训练 group with the 数据集 entry while keeping the existing 5 menu items', () => {
    render(<App />)

    expect(screen.getByText('训练')).toBeInTheDocument()
    expect(screen.getByText('数据集')).toBeInTheDocument()

    expect(screen.getByText('订阅管理')).toBeInTheDocument()
    expect(screen.getByText('录制状态')).toBeInTheDocument()
    expect(screen.getByText('Transcripts')).toBeInTheDocument()
    expect(screen.getByText('Cookie 管理')).toBeInTheDocument()
    expect(screen.getByText('通知')).toBeInTheDocument()
  })

  it('should switch to the training datasets page when the 数据集 entry is clicked', () => {
    render(<App />)

    fireEvent.click(screen.getByText('数据集'))

    expect(screen.getByTestId('training-datasets-page')).toBeInTheDocument()
    expect(screen.queryByTestId('subscription-page')).not.toBeInTheDocument()
    expect(screen.getByText('数据集').closest('li')).toHaveClass('ant-menu-item-selected')
  })

  it('should render subscription page by default', () => {
    render(<App />)

    expect(screen.getByTestId('subscription-page')).toBeInTheDocument()
    expect(screen.queryByTestId('cookie-page')).not.toBeInTheDocument()
    expect(screen.queryByTestId('notification-page')).not.toBeInTheDocument()
  })

  it('should switch to cookie page when menu item is clicked', () => {
    render(<App />)

    fireEvent.click(screen.getByText('Cookie 管理'))

    expect(screen.getByTestId('cookie-page')).toBeInTheDocument()
    expect(screen.queryByTestId('subscription-page')).not.toBeInTheDocument()
  })

  it('should switch to notification page when menu item is clicked', () => {
    render(<App />)

    fireEvent.click(screen.getByText('通知'))

    expect(screen.getByTestId('notification-page')).toBeInTheDocument()
    expect(screen.queryByTestId('subscription-page')).not.toBeInTheDocument()
  })

  it('should highlight current menu item', () => {
    render(<App />)

    const subscriptionMenuItem = screen.getByText('订阅管理')
    expect(subscriptionMenuItem.closest('li')).toHaveClass('ant-menu-item-selected')

    fireEvent.click(screen.getByText('Cookie 管理'))

    const cookieMenuItem = screen.getByText('Cookie 管理')
    expect(cookieMenuItem.closest('li')).toHaveClass('ant-menu-item-selected')
    expect(subscriptionMenuItem.closest('li')).not.toHaveClass('ant-menu-item-selected')
  })

  it('should switch back to subscription page when subscription menu is clicked', () => {
    render(<App />)

    fireEvent.click(screen.getByText('Cookie 管理'))
    fireEvent.click(screen.getByText('订阅管理'))

    expect(screen.getByTestId('subscription-page')).toBeInTheDocument()
    expect(screen.queryByTestId('cookie-page')).not.toBeInTheDocument()
  })
})
