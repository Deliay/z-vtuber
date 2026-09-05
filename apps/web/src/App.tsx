import { useState } from 'react'
import { SubscriptionPage } from './pages/subscription/SubscriptionPage'
import { CookiePage } from './pages/cookie/CookiePage'
import './App.css'

type PageKey = 'subscriptions' | 'cookies'

interface MenuItem {
  key: PageKey
  label: string
  component: React.ComponentType
}

const menuItems: MenuItem[] = [
  { key: 'subscriptions', label: '订阅管理', component: SubscriptionPage },
  { key: 'cookies', label: 'Cookie 管理', component: CookiePage },
]

function App() {
  const [currentPage, setCurrentPage] = useState<PageKey>('subscriptions')

  const CurrentComponent =
    menuItems.find((item) => item.key === currentPage)?.component ??
    SubscriptionPage

  return (
    <div className="app">
      <nav className="sidebar">
        <h1>Z.Vtuber 管理后台</h1>
        <div className="menu">
          {menuItems.map((item) => (
            <a
              key={item.key}
              className="menu-item"
              aria-current={currentPage === item.key ? 'page' : undefined}
              onClick={() => setCurrentPage(item.key)}
            >
              {item.label}
            </a>
          ))}
        </div>
      </nav>
      <main className="content">
        <CurrentComponent />
      </main>
    </div>
  )
}

export default App
