import { useState } from 'react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { SubscriptionPage } from './pages/subscription/SubscriptionPage'
import { CookiePage } from './pages/cookie/CookiePage'
import { NotificationPage } from './pages/notification/NotificationPage'
import { RecordingStatusPage } from './pages/recording/RecordingStatusPage'
import { TranscriptsPage } from './pages/transcripts/TranscriptsPage'
import { DatasetsPage } from './pages/training/DatasetsPage'
import { AppShell, type AppShellMenuGroup } from './components/AppShell'
import { MENU_ICONS } from './components/menuIcons'
import { appTheme } from './theme'
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

/** 把业务菜单结构与图标合并，交给 `AppShell` 渲染。 */
const shellMenuGroups: AppShellMenuGroup[] = menuGroups.map((group) => ({
  group: group.group,
  items: group.items.map((item) => ({
    key: item.key,
    label: item.label,
    icon: MENU_ICONS[item.key],
  })),
}))

function App() {
  const [currentPage, setCurrentPage] = useState<PageKey>('subscriptions')

  const CurrentComponent =
    menuItems.find((item) => item.key === currentPage)?.component ??
    SubscriptionPage

  return (
    <ConfigProvider locale={zhCN} theme={appTheme}>
      <AntdApp>
        <AppShell
          menuGroups={shellMenuGroups}
          currentPage={currentPage}
          onSelect={(key) => setCurrentPage(key as PageKey)}
        >
          <CurrentComponent />
        </AppShell>
      </AntdApp>
    </ConfigProvider>
  )
}

export default App
