import type { CSSProperties } from 'react'
import { Tooltip } from 'antd'

export interface TruncatedTextProps {
  /** 完整文本：限宽省略号展示，全文经 Tooltip 提供。 */
  text: string
  /** 附加内联样式（如宽度约束、flex 等）。 */
  style?: CSSProperties
  className?: string
}

/**
 * 限宽截断文本：省略号展示，antd `Tooltip` 在 hover 与键盘 focus 时均展示完整内容，
 * `span` 以 `tabIndex={0}` 可聚焦，满足「悬浮提示与完整信息对键盘用户同样可达」。
 */
export function TruncatedText({ text, style, className }: TruncatedTextProps) {
  return (
    <Tooltip title={text} trigger={['hover', 'focus']}>
      <span
        className={className ? `z-truncated ${className}` : 'z-truncated'}
        style={style}
        tabIndex={0}
      >
        {text}
      </span>
    </Tooltip>
  )
}
