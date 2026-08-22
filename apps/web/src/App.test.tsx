import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import App from './App'

vi.mock('./pages/subscription/SubscriptionPage', () => ({
  SubscriptionPage: () => <div data-testid="subscription-page">订阅管理页面</div>,
}))

vi.mock('./pages/cookie/CookiePage', () => ({
  CookiePage: () => <div data-testid="cookie-page">Cookie 管理页面</div>,
}))

describe('App', () => {
  it('should render sidebar with menu items', () => {
    render(<App />)

    expect(screen.getByText('Z.Vtuber 管理后台')).toBeInTheDocument()
    expect(screen.getByText('订阅管理')).toBeInTheDocument()
    expect(screen.getByText('Cookie 管理')).toBeInTheDocument()
  })

  it('should render subscription page by default', () => {
    render(<App />)

    expect(screen.getByTestId('subscription-page')).toBeInTheDocument()
    expect(screen.queryByTestId('cookie-page')).not.toBeInTheDocument()
  })

  it('should switch to cookie page when menu item is clicked', () => {
    render(<App />)

    fireEvent.click(screen.getByText('Cookie 管理'))

    expect(screen.getByTestId('cookie-page')).toBeInTheDocument()
    expect(screen.queryByTestId('subscription-page')).not.toBeInTheDocument()
  })

  it('should highlight current menu item', () => {
    render(<App />)

    const subscriptionMenuItem = screen.getByText('订阅管理')
    expect(subscriptionMenuItem).toHaveAttribute('aria-current', 'page')

    fireEvent.click(screen.getByText('Cookie 管理'))

    const cookieMenuItem = screen.getByText('Cookie 管理')
    expect(cookieMenuItem).toHaveAttribute('aria-current', 'page')
    expect(subscriptionMenuItem).not.toHaveAttribute('aria-current')
  })

  it('should switch back to subscription page when subscription menu is clicked', () => {
    render(<App />)

    fireEvent.click(screen.getByText('Cookie 管理'))
    fireEvent.click(screen.getByText('订阅管理'))

    expect(screen.getByTestId('subscription-page')).toBeInTheDocument()
    expect(screen.queryByTestId('cookie-page')).not.toBeInTheDocument()
  })
})
