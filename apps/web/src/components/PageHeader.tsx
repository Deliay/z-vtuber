import type { ReactNode } from 'react'
import { Typography } from 'antd'

export interface PageHeaderProps {
  /** 页面标题（与既有页面 `<h2>` 文案一致）。 */
  title: string
  /** 一句话说明（可选）。 */
  description?: string
  /** 主操作区（按钮/切换等）。 */
  extra?: ReactNode
}

/**
 * 页头：标题 + 一句话说明 + 主操作区。
 * 全站页面统一使用，标题语义为 `h2` 以兼容既有可访问名查询。
 */
export function PageHeader({ title, description, extra }: PageHeaderProps) {
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'flex-start',
        justifyContent: 'space-between',
        gap: 16,
        marginBottom: 16,
        flexWrap: 'wrap',
      }}
    >
      <div>
        <Typography.Title level={2} style={{ margin: 0 }}>
          {title}
        </Typography.Title>
        {description && (
          <Typography.Text type="secondary">{description}</Typography.Text>
        )}
      </div>
      {extra && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>{extra}</div>
      )}
    </div>
  )
}
