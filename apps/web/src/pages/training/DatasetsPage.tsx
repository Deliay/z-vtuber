import { useState } from 'react'
import { Button, Space, Tabs } from 'antd'
import { DownloadOutlined } from '@ant-design/icons'
import { DatasetSelector } from './DatasetSelector'
import { DatasetItemList } from './DatasetItemList'
import { TranscriptImportDialog } from './TranscriptImportDialog'
import { ModelSettingsTab } from './ModelSettingsTab'
import { EmotionTaskQueue } from './EmotionTaskQueue'
import { PageHeader } from '../../components/PageHeader'

type TabKey = 'audio' | 'models'

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

  const audioTab = (
    <>
      <DatasetSelector selectedId={selectedId} onSelect={setSelectedId} />

      {selectedId && (
        <Space style={{ margin: '16px 0' }}>
          <Button icon={<DownloadOutlined />} onClick={() => setImportOpen(true)}>
            从 Transcript 导入
          </Button>
        </Space>
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
  )

  return (
    <div>
      <PageHeader title="数据集" description="管理音频数据集与识别/情绪模型。" />

      <Tabs
        activeKey={activeTab}
        onChange={(key) => setActiveTab(key as TabKey)}
        items={[
          { key: 'audio', label: '音频数据集', children: audioTab },
          { key: 'models', label: '模型设置', children: <ModelSettingsTab /> },
        ]}
      />
    </div>
  )
}
