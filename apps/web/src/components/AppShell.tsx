import { Layout, Menu, theme, Typography, type MenuProps } from 'antd'
import { ThunderboltOutlined } from '@ant-design/icons'
import type { ReactNode } from 'react'

/** antd Menu 项类型别名。 */
type AntdMenuItem = NonNullable<MenuProps['items']>[number]

export interface AppShellMenuItem {
  key: string
  label: ReactNode
  icon?: ReactNode
}

export interface AppShellMenuGroup {
  /** 分组标题（缺省 = 默认区，不渲染分组标题）。 */
  group?: string
  items: AppShellMenuItem[]
}

export interface AppShellProps {
  menuGroups: AppShellMenuGroup[]
  /** 当前选中页（MenuItem.key）。 */
  currentPage: string
  onSelect: (key: string) => void
  children: ReactNode
}

const { Sider, Content, Header } = Layout

/** 缺省组渲染为无分组标题的菜单项集合。 */
function buildMenuItems(menuGroups: AppShellMenuGroup[]): MenuProps['items'] {
  const children: AntdMenuItem[] = []

  // 缺省组（无 group 标题）的项平铺在顶层；有分组标题的项包一层分组。
  for (const group of menuGroups) {
    if (!group.group) {
      children.push(
        ...group.items.map((item) => ({
          key: item.key,
          label: item.label,
          icon: item.icon,
        })),
      )
    }
  }
  for (const group of menuGroups) {
    if (group.group) {
      children.push({
        key: `__group__${group.group}`,
        type: 'group',
        label: group.group,
        children: group.items.map((item) => ({
          key: item.key,
          label: item.label,
          icon: item.icon,
        })),
      })
    }
  }

  return children
}

/**
 * 应用壳层：左侧 `Sider` + `Menu` 导航、顶部品牌头、右侧内容区。
 * 菜单分组与默认页由 `App.tsx` 传入，UI 只负责渲染与选中态。
 */
export function AppShell({ menuGroups, currentPage, onSelect, children }: AppShellProps) {
  const items = buildMenuItems(menuGroups)
  // 颜色/边框统一取自 Design Token，避免绕过 theme.ts 写死色值。
  const { token } = theme.useToken()

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider
        width={220}
        breakpoint="lg"
        collapsedWidth={0}
        theme="dark"
        style={{ borderInlineEnd: `1px solid ${token.colorBorder}` }}
      >
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 8,
            height: 56,
            paddingInline: 16,
            color: token.colorPrimary,
            fontWeight: 600,
            fontSize: 16,
          }}
        >
          <ThunderboltOutlined />
          <span>Z.Vtuber 管理后台</span>
        </div>
        <Menu
          mode="inline"
          theme="dark"
          selectedKeys={[currentPage]}
          items={items}
          onClick={({ key }) => onSelect(key)}
          style={{ borderInlineEnd: 'none', background: 'transparent' }}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            display: 'flex',
            alignItems: 'center',
            height: 56,
            paddingInline: 24,
            background: token.colorBgContainer,
            borderBottom: `1px solid ${token.colorBorder}`,
          }}
        >
          <Typography.Text strong>管理后台</Typography.Text>
        </Header>
        <Content style={{ padding: 24, overflow: 'auto' }}>{children}</Content>
      </Layout>
    </Layout>
  )
}
