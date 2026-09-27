import { useState } from 'react'
import { SubscriptionPage } from './pages/subscription/SubscriptionPage'
import { CookiePage } from './pages/cookie/CookiePage'
import { NotificationPage } from './pages/notification/NotificationPage'
import { RecordingStatusPage } from './pages/recording/RecordingStatusPage'
import { TranscriptsPage } from './pages/transcripts/TranscriptsPage'
import { DatasetsPage } from './pages/training/DatasetsPage'
import './App.css'

type PageKey =
  | 'subscriptions'
  | 'recordings'
  | 'transcripts'
  | 'cookies'
  | 'notifications'
  | 'training-datasets'

interface MenuItem {
  key: PageKey
  label: string
  component: React.ComponentType
}

interface MenuGroup {
  /** 分组标题（缺省 = 既有默认区，不渲染分组标题）。 */
  group?: string
  items: MenuItem[]
}

const menuGroups: MenuGroup[] = [
  {
    items: [
      { key: 'subscriptions', label: '订阅管理', component: SubscriptionPage },
      { key: 'recordings', label: '录制状态', component: RecordingStatusPage },
      { key: 'transcripts', label: 'Transcripts', component: TranscriptsPage },
      { key: 'cookies', label: 'Cookie 管理', component: CookiePage },
      { key: 'notifications', label: '通知', component: NotificationPage },
    ],
  },
  {
    group: '训练',
    items: [{ key: 'training-datasets', label: '数据集', component: DatasetsPage }],
  },
]

const menuItems: MenuItem[] = menuGroups.flatMap((group) => group.items)

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
          {menuGroups.map((group) => (
            <div className="menu-group" key={group.group ?? 'default'}>
              {group.group && <div className="menu-group-label">{group.group}</div>}
              {group.items.map((item) => (
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
