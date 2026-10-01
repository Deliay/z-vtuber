import { useState } from 'react'
import { Tabs } from 'antd'
import { TaskListTab } from './TaskListTab'
import { SettingsTab } from './SettingsTab'
import { PageHeader } from '../../components/PageHeader'

type TabKey = 'tasks' | 'settings'

export function TranscriptsPage() {
  const [activeTab, setActiveTab] = useState<TabKey>('tasks')

  return (
    <div>
      <PageHeader title="语音转写" description="管理识别任务与自动识别设置。" />

      <Tabs
        activeKey={activeTab}
        onChange={(key) => setActiveTab(key as TabKey)}
        items={[
          { key: 'tasks', label: '任务列表', children: <TaskListTab /> },
          { key: 'settings', label: '设置', children: <SettingsTab /> },
        ]}
      />
    </div>
  )
}
