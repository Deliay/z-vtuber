import {
  BellOutlined,
  CloudServerOutlined,
  DatabaseOutlined,
  FileTextOutlined,
  VideoCameraOutlined,
  ApiOutlined,
} from '@ant-design/icons'
import type { ReactNode } from 'react'

/** 菜单项 key（与 `App.tsx` 的 `PageKey` 对应）。 */
export type MenuIconKey =
  | 'subscriptions'
  | 'recordings'
  | 'transcripts'
  | 'cookies'
  | 'notifications'
  | 'training-datasets'

/** 与菜单 key 一一对应的图标（供 `App.tsx` 复用）。 */
export const MENU_ICONS: Record<MenuIconKey, ReactNode> = {
  subscriptions: <CloudServerOutlined />,
  recordings: <VideoCameraOutlined />,
  transcripts: <FileTextOutlined />,
  cookies: <ApiOutlined />,
  notifications: <BellOutlined />,
  'training-datasets': <DatabaseOutlined />,
}
