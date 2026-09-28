import { useState } from 'react'
import { DatasetSelector } from './DatasetSelector'
import { DatasetItemList } from './DatasetItemList'
import { TranscriptImportDialog } from './TranscriptImportDialog'
import { ModelSettingsTab } from './ModelSettingsTab'
import { EmotionTaskQueue } from './EmotionTaskQueue'

type TabKey = 'audio' | 'models'

const TABS: { key: TabKey; label: string }[] = [
  { key: 'audio', label: '音频数据集' },
  { key: 'models', label: '模型设置' },
]

/**
 * 训练 → 数据集：多 Tab 容器（「音频数据集」/「模型设置」）。
 * 「音频数据集」Tab 内为数据集下拉管理 + 从 Transcript 导入 + 音频数据列表 + 情绪任务队列；
 * 「模型设置」Tab 内为 Providers / Models 管理。
 */
export function DatasetsPage() {
  const [activeTab, setActiveTab] = useState<TabKey>('audio')
  const [selectedId, setSelectedId] = useState('')
  const [reloadToken, setReloadToken] = useState(0)
  const [queueToken, setQueueToken] = useState(0)
  const [importOpen, setImportOpen] = useState(false)

  return (
    <div>
      <h2>数据集</h2>

      <div
        role="tablist"
        style={{ display: 'flex', gap: '0.5rem', marginBottom: '1rem' }}
      >
        {TABS.map((tab) => (
          <button
            key={tab.key}
            role="tab"
            aria-selected={activeTab === tab.key}
            onClick={() => setActiveTab(tab.key)}
            style={{
              padding: '0.5rem 1rem',
              border: 'none',
              borderRadius: '0.25rem',
              cursor: 'pointer',
              backgroundColor: activeTab === tab.key ? '#38bdf8' : '#334155',
              color: activeTab === tab.key ? '#0f172a' : '#e2e8f0',
            }}
          >
            {tab.label}
          </button>
        ))}
      </div>

      {activeTab === 'models' ? (
        <ModelSettingsTab />
      ) : (
        <>
          <DatasetSelector selectedId={selectedId} onSelect={setSelectedId} />

          {selectedId && (
            <div style={{ margin: '1rem 0' }}>
              <button onClick={() => setImportOpen(true)}>从 Transcript 导入</button>
            </div>
          )}

          {selectedId && (
            <DatasetItemList
              key={selectedId}
              datasetId={selectedId}
              reloadToken={reloadToken}
              onChanged={() => setQueueToken((token) => token + 1)}
            />
          )}

          {selectedId && <EmotionTaskQueue datasetId={selectedId} reloadToken={queueToken} />}

          {selectedId && importOpen && (
            <TranscriptImportDialog
              datasetId={selectedId}
              onClose={() => setImportOpen(false)}
              onImported={() => setReloadToken((token) => token + 1)}
            />
          )}
        </>
      )}
    </div>
  )
}
