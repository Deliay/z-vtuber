import { useState } from 'react'
import { TaskListTab } from './TaskListTab'
import { SettingsTab } from './SettingsTab'

type TabKey = 'tasks' | 'settings'

const TABS: { key: TabKey; label: string }[] = [
  { key: 'tasks', label: '任务列表' },
  { key: 'settings', label: '设置' },
]

export function TranscriptsPage() {
  const [activeTab, setActiveTab] = useState<TabKey>('tasks')

  return (
    <div>
      <h2>语音转写</h2>

      <div role="tablist" style={{ display: 'flex', gap: '0.5rem', marginBottom: '1rem' }}>
        {TABS.map((tab) => {
          const active = activeTab === tab.key
          return (
            <button
              key={tab.key}
              role="tab"
              aria-selected={active}
              onClick={() => setActiveTab(tab.key)}
              style={{
                padding: '0.5rem 1rem',
                border: 'none',
                borderRadius: '0.25rem',
                cursor: 'pointer',
                backgroundColor: active ? '#38bdf8' : '#334155',
                color: active ? '#0f172a' : '#e2e8f0',
              }}
            >
              {tab.label}
            </button>
          )
        })}
      </div>

      {activeTab === 'tasks' ? <TaskListTab /> : <SettingsTab />}
    </div>
  )
}
