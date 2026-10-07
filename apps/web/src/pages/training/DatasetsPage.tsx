import { useState } from 'react'
import { Button, Space, Tabs, theme, Tooltip, Typography } from 'antd'
import { DownloadOutlined } from '@ant-design/icons'
import { trainingDatasetExportUrl } from '../../api/client'
import { DatasetSelector } from './DatasetSelector'
import { DatasetItemList } from './DatasetItemList'
import { TranscriptImportDialog } from './TranscriptImportDialog'
import { ModelSettingsTab } from './ModelSettingsTab'
import { EmotionTaskQueue } from './EmotionTaskQueue'
import { PageHeader } from '../../components/PageHeader'

type TabKey = 'audio' | 'models'

/**
 * 训练 → 数据集：多 Tab 容器（「音频数据集」/「模型设置」）。
 * 「音频数据集」Tab 内为数据集下拉管理 + 下载数据集 + 从 Transcript 导入 + 音频数据列表 + 情绪任务队列；
 * 「模型设置」Tab 内为 Providers / Models 管理。
 */
export function DatasetsPage() {
  const { token } = theme.useToken()
  const [activeTab, setActiveTab] = useState<TabKey>('audio')
  const [selectedId, setSelectedId] = useState('')
  const [reloadToken, setReloadToken] = useState(0)
  const [queueToken, setQueueToken] = useState(0)
  const [importOpen, setImportOpen] = useState(false)
  const [itemCount, setItemCount] = useState(0)

  // 切换数据集时立即清零条目数：避免旧数据集计数残留导致新数据集按钮在列表返回前短暂可用；
  // 新计数随后由 DatasetItemList 加载成功后经 onItemsLoaded 上提（加载失败按 0 上报）。
  const handleSelectDataset = (datasetId: string) => {
    setSelectedId(datasetId)
    setItemCount(0)
  }

  // 未选定数据集或数据集为空时禁用下载；按钮始终渲染（PRD 功能点 1）。
  const exportDisabled = !selectedId || itemCount === 0
  const exportLink = (
    <Typography.Link
      href={selectedId ? trainingDatasetExportUrl(selectedId) : undefined}
      download
      aria-disabled={exportDisabled ? true : undefined}
      style={exportDisabled ? { color: token.colorTextDisabled } : undefined}
      onClick={(event) => {
        if (exportDisabled) {
          event.preventDefault()
        }
      }}
    >
      下载数据集
    </Typography.Link>
  )

  const audioTab = (
    <>
      <DatasetSelector selectedId={selectedId} onSelect={handleSelectDataset} />

      <Space style={{ margin: '16px 0' }}>
        {exportDisabled ? (
          // 置灰说明对键盘用户同样可达（focus 即出 Tooltip）。
          <Tooltip title="请先选择非空数据集" trigger={['hover', 'focus']}>
            {exportLink}
          </Tooltip>
        ) : (
          exportLink
        )}

        {selectedId && (
          <Button icon={<DownloadOutlined />} onClick={() => setImportOpen(true)}>
            从 Transcript 导入
          </Button>
        )}
      </Space>

      {selectedId && (
        <DatasetItemList
          key={selectedId}
          datasetId={selectedId}
          reloadToken={reloadToken}
          onChanged={() => setQueueToken((token) => token + 1)}
          onItemsLoaded={setItemCount}
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
