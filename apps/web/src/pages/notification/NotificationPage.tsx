import { useEffect, useState } from 'react'
import {
  Alert,
  App as AntdApp,
  Button,
  Empty,
  Form,
  Modal,
  Select,
  Spin,
  Table,
  Typography,
} from 'antd'
import { PlusOutlined } from '@ant-design/icons'
import type { ColumnsType } from 'antd/es/table'
import {
  listNotifyGroups,
  listNotificationBindings,
  createNotificationBinding,
  deleteNotificationBinding,
} from '../../api/client'
import type { components } from '../../api/schema-notify'
import { PageHeader } from '../../components/PageHeader'

type NotifyGroup = components['schemas']['NotifyGroup']
type NotificationSubscription = components['schemas']['NotificationSubscription']

const EVENT_TYPE_OPTIONS = [
  { value: 'bilibili_cookie_invalid', label: 'bilibili帐号失效通知' },
  { value: 'weibo_cookie_invalid', label: '微博帐号失效通知' },
] as const

const EVENT_TYPE_LABELS: Record<string, string> = {
  bilibili_cookie_invalid: 'bilibili帐号失效通知',
  weibo_cookie_invalid: '微博帐号失效通知',
  cookie_invalid: 'bilibili帐号失效通知',
}

function formatDate(dateString?: string | null) {
  if (!dateString) return '-'
  return new Date(dateString).toLocaleString()
}

export function NotificationPage() {
  const { message, modal } = AntdApp.useApp()
  const [bindings, setBindings] = useState<NotificationSubscription[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [showCreateModal, setShowCreateModal] = useState(false)
  const [groups, setGroups] = useState<NotifyGroup[]>([])
  const [groupsLoading, setGroupsLoading] = useState(false)
  const [groupsError, setGroupsError] = useState<string | null>(null)
  const [selectedGroup, setSelectedGroup] = useState<string>('')
  const [selectedEventType, setSelectedEventType] = useState<string>('bilibili_cookie_invalid')
  const [submitting, setSubmitting] = useState(false)

  const fetchBindings = async () => {
    try {
      setLoading(true)
      setError(null)
      const data = await listNotificationBindings()
      setBindings(data)
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchBindings()
  }, [])

  const handleOpenCreateModal = async () => {
    setShowCreateModal(true)
    setGroupsLoading(true)
    setGroupsError(null)
    setSelectedGroup('')
    setSelectedEventType('bilibili_cookie_invalid')
    try {
      const data = await listNotifyGroups()
      setGroups(data)
    } catch {
      setGroupsError('加载群列表失败')
    } finally {
      setGroupsLoading(false)
    }
  }

  const handleCreate = async () => {
    if (!selectedGroup) {
      message.error('请选择群')
      return
    }

    const group = groups.find((g) => g.groupId === selectedGroup)
    if (!group) {
      message.error('群不存在')
      return
    }

    try {
      setSubmitting(true)
      await createNotificationBinding({
        eventType: selectedEventType as 'bilibili_cookie_invalid' | 'weibo_cookie_invalid',
        groupId: group.groupId,
        groupName: group.groupName,
      })
      message.success('绑定已创建')
      setShowCreateModal(false)
      await fetchBindings()
    } catch (err: any) {
      if (err.status === 400 && err.serverMessage) {
        message.error(err.serverMessage)
      } else if (err.serverMessage?.includes('已绑定')) {
        message.error('该群已绑定')
      } else {
        message.error('创建失败，请重试')
      }
    } finally {
      setSubmitting(false)
    }
  }

  const handleDelete = (binding: NotificationSubscription) => {
    modal.confirm({
      title: '确认删除该通知绑定？',
      content: '删除后该群将不再收到对应失效通知。',
      okText: '确认删除',
      cancelText: '返回',
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          await deleteNotificationBinding(binding.id!)
          message.success('绑定已删除')
          await fetchBindings()
        } catch (err: any) {
          if (err.status === 404) {
            message.error('该绑定已不存在')
            await fetchBindings()
          } else {
            message.error('删除失败，请重试')
          }
        }
      },
    })
  }

  const columns: ColumnsType<NotificationSubscription> = [
    {
      title: '通知类型',
      dataIndex: 'eventType',
      render: (value: string) => EVENT_TYPE_LABELS[value ?? ''] ?? value,
    },
    {
      title: '目标群',
      dataIndex: 'groupName',
      render: (_value, record) =>
        `${record.groupName ?? record.groupId}（${record.groupId}）`,
    },
    {
      title: '绑定时间',
      dataIndex: 'createdAt',
      render: (value: string) => formatDate(value),
    },
    {
      title: '操作',
      key: 'action',
      width: 120,
      render: (_value, record) => (
        <Button danger onClick={() => handleDelete(record)}>
          删除
        </Button>
      ),
    },
  ]

  return (
    <div>
      <PageHeader
        title="通知管理"
        description="配置帐号失效通知的目标群绑定。"
        extra={
          <Button type="primary" icon={<PlusOutlined />} onClick={handleOpenCreateModal}>
            新增绑定
          </Button>
        }
      />

      {error ? (
        <Alert
          type="error"
          showIcon
          message={error}
          action={<Button onClick={fetchBindings}>重试</Button>}
        />
      ) : (
        <Table<NotificationSubscription>
          rowKey={(record) => record.id ?? ''}
          columns={columns}
          dataSource={bindings}
          loading={loading}
          pagination={false}
          locale={{
            emptyText: (
              <Empty description="暂无通知绑定">
                <Button type="primary" onClick={handleOpenCreateModal}>
                  新增绑定
                </Button>
              </Empty>
            ),
          }}
        />
      )}

      <Modal
        title="新增绑定"
        open={showCreateModal}
        onOk={handleCreate}
        onCancel={() => setShowCreateModal(false)}
        confirmLoading={submitting}
        okText="创建"
        cancelText="取消"
        okButtonProps={{ disabled: groupsLoading }}
        destroyOnHidden
      >
        <Form layout="vertical">
          <Form.Item label="通知类型">
            <Select
              value={selectedEventType}
              onChange={setSelectedEventType}
              options={EVENT_TYPE_OPTIONS.map((option) => ({ ...option }))}
            />
          </Form.Item>

          <Form.Item label="选择群" required>
            {groupsLoading ? (
              <Spin />
            ) : groupsError ? (
              <Typography.Text type="danger">{groupsError}</Typography.Text>
            ) : (
              <Select
                value={selectedGroup || undefined}
                onChange={setSelectedGroup}
                placeholder="请选择群"
                options={groups.map((group) => ({
                  value: group.groupId,
                  label: `${group.groupName ?? group.groupId}（${group.groupId}）`,
                }))}
              />
            )}
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
