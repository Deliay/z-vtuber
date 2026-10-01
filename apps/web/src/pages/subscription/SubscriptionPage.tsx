import { useEffect, useState } from 'react'
import {
  Alert,
  App as AntdApp,
  Button,
  Empty,
  Form,
  Input,
  Select,
  Table,
  Tag,
} from 'antd'
import { PlusOutlined } from '@ant-design/icons'
import type { ColumnsType } from 'antd/es/table'
import {
  listBackstageSubscriptions,
  listWeiboSubscriptions,
  subscribe,
  subscribeWeibo,
  unsubscribe,
  unsubscribeWeibo,
} from '../../api/client'
import { PageHeader } from '../../components/PageHeader'

type Platform = 'bilibili' | 'weibo'

interface Row {
  key: string
  platform: Platform
  id: number
  type?: 'danmaku' | 'dynamic' | 'guard' | 'recording'
  subscribedAt?: string
  name?: string | null
}

const TYPE_LABELS: Record<string, string> = {
  danmaku: '弹幕',
  dynamic: '动态',
  guard: '舰长',
  recording: '录播',
}

const PLATFORM_LABELS: Record<Platform, string> = {
  bilibili: 'B站',
  weibo: '微博',
}

const PLATFORM_COLORS: Record<Platform, string> = {
  bilibili: 'blue',
  weibo: 'orange',
}

const TYPE_OPTIONS = [
  { value: 'danmaku', label: '弹幕' },
  { value: 'dynamic', label: '动态' },
  { value: 'guard', label: '舰长' },
  { value: 'recording', label: '录播' },
]

function formatDate(dateString?: string | null) {
  if (!dateString) return '-'
  return new Date(dateString).toLocaleString()
}

export function SubscriptionPage() {
  const { message, modal } = AntdApp.useApp()
  const [form] = Form.useForm()

  const [rows, setRows] = useState<Row[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [platform, setPlatform] = useState<Platform>('bilibili')
  const [submitting, setSubmitting] = useState(false)

  const fetchSubscriptions = async () => {
    try {
      setLoading(true)
      setError(null)
      const [biliItems, weiboItems] = await Promise.all([
        listBackstageSubscriptions(),
        listWeiboSubscriptions(),
      ])

      const biliRows: Row[] = (biliItems ?? []).map((item) => ({
        key: `bilibili-${item.mid}-${item.type}-${item.subscribedAt ?? ''}`,
        platform: 'bilibili' as const,
        id: item.mid!,
        type: (item.type ?? 'dynamic') as 'danmaku' | 'dynamic' | 'guard' | 'recording',
        subscribedAt: item.subscribedAt,
        name: item.streamer?.name,
      }))

      const weiboRows: Row[] = (weiboItems ?? []).map((item) => ({
        key: `weibo-${item.uid}`,
        platform: 'weibo' as const,
        id: item.uid!,
        subscribedAt: item.subscribedAt,
      }))

      setRows([...biliRows, ...weiboRows])
    } catch {
      setError('服务不可用')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchSubscriptions()
  }, [])

  const handleSubscribe = async (values: {
    mid?: string
    uid?: string
    type?: string
  }) => {
    try {
      setSubmitting(true)
      if (platform === 'bilibili') {
        const midNum = Number(values.mid)
        await subscribe({ mid: midNum, type: values.type as 'danmaku' })
        form.setFieldsValue({ mid: undefined, type: undefined })
      } else {
        const uidNum = Number(values.uid)
        await subscribeWeibo({ uid: uidNum })
        form.setFieldsValue({ uid: undefined })
      }
      message.success('订阅已创建')
      await fetchSubscriptions()
    } catch (err: any) {
      if (err.status === 400 && err.serverMessage) {
        message.error(err.serverMessage)
      } else {
        message.error('提交失败，请重试')
      }
    } finally {
      setSubmitting(false)
    }
  }

  const handleCancel = (row: Row) => {
    modal.confirm({
      title: '确认取消该订阅？',
      content: `取消后将不再收到该${PLATFORM_LABELS[row.platform]}订阅的更新。`,
      okText: '确认取消',
      cancelText: '返回',
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          if (row.platform === 'weibo') {
            await unsubscribeWeibo(row.id)
          } else if (row.type) {
            await unsubscribe(row.id, row.type)
          }
          message.success('订阅已取消')
          await fetchSubscriptions()
        } catch (err: any) {
          if (err.status === 404) {
            message.error('该订阅已不存在')
            await fetchSubscriptions()
          } else {
            message.error('取消订阅失败，请重试')
          }
        }
      },
    })
  }

  const columns: ColumnsType<Row> = [
    {
      title: '平台',
      dataIndex: 'platform',
      width: 90,
      render: (value: Platform) => (
        <Tag color={PLATFORM_COLORS[value]}>{PLATFORM_LABELS[value]}</Tag>
      ),
    },
    {
      title: '主播昵称',
      dataIndex: 'name',
      render: (_value, record) =>
        record.platform === 'bilibili' ? (record.name ?? '暂无资料') : '-',
    },
    { title: '主播 ID', dataIndex: 'id', width: 120 },
    {
      title: '类型',
      dataIndex: 'type',
      width: 100,
      render: (_value, record) =>
        record.platform === 'weibo'
          ? '微博动态'
          : (TYPE_LABELS[record.type ?? ''] ?? record.type),
    },
    {
      title: '订阅时间',
      dataIndex: 'subscribedAt',
      width: 200,
      render: (value: string) => formatDate(value),
    },
    {
      title: '操作',
      key: 'action',
      width: 100,
      render: (_value, record) => (
        <Button danger onClick={() => handleCancel(record)}>
          取消
        </Button>
      ),
    },
  ]

  return (
    <div>
      <PageHeader title="订阅管理" description="管理 B站 / 微博的主播与微博订阅。" />

      <Form
        form={form}
        layout="inline"
        onFinish={handleSubscribe}
        style={{ marginBottom: 16, rowGap: 12 }}
      >
        <Form.Item label="订阅平台" style={{ marginBottom: 0 }}>
          <Select
            aria-label="订阅平台"
            value={platform}
            onChange={(value) => {
              const nextPlatform = value as Platform
              setPlatform(nextPlatform)
              form.resetFields()
            }}
            options={[
              { value: 'bilibili', label: 'B站' },
              { value: 'weibo', label: '微博' },
            ]}
            style={{ width: 100 }}
          />
        </Form.Item>

        {platform === 'bilibili' ? (
          <>
            <Form.Item
              name="mid"
              rules={[
                { required: true, message: '请输入有效的主播 ID（纯数字）' },
                { pattern: /^\d+$/, message: '请输入有效的主播 ID（纯数字）' },
                {
                  validator: (_rule, value) =>
                    value && Number(value) <= 0
                      ? Promise.reject(new Error('主播 ID 必须为正整数'))
                      : Promise.resolve(),
                },
              ]}
              style={{ marginBottom: 0 }}
            >
              <Input placeholder="主播 ID (mid)" style={{ width: 160 }} />
            </Form.Item>
            <Form.Item
              name="type"
              rules={[{ required: true, message: '请选择订阅类型' }]}
              style={{ marginBottom: 0 }}
            >
              <Select
                placeholder="请选择类型"
                options={TYPE_OPTIONS}
                style={{ width: 130 }}
                aria-label="订阅类型"
              />
            </Form.Item>
          </>
        ) : (
          <Form.Item
            name="uid"
            rules={[
              { required: true, message: '请输入有效的微博用户 ID（纯数字）' },
              { pattern: /^\d+$/, message: '请输入有效的微博用户 ID（纯数字）' },
              {
                validator: (_rule, value) =>
                  value && Number(value) <= 0
                    ? Promise.reject(new Error('微博用户 ID 必须为正整数'))
                    : Promise.resolve(),
              },
            ]}
            style={{ marginBottom: 0 }}
          >
            <Input placeholder="微博用户 ID (uid)" style={{ width: 180 }} />
          </Form.Item>
        )}

        <Form.Item style={{ marginBottom: 0 }}>
          <Button type="primary" htmlType="submit" icon={<PlusOutlined />} loading={submitting}>
            新增订阅
          </Button>
        </Form.Item>
      </Form>

      {error ? (
        <Alert
          type="error"
          showIcon
          title={error}
          action={<Button onClick={fetchSubscriptions}>重试</Button>}
        />
      ) : (
        <Table<Row>
          rowKey="key"
          columns={columns}
          dataSource={rows}
          loading={loading}
          pagination={false}
          locale={{
            emptyText: <Empty description="暂无订阅" />,
          }}
        />
      )}
    </div>
  )
}
