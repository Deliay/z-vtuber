import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { AppShell, type AppShellMenuGroup } from './AppShell'
import { MENU_ICONS } from './menuIcons'

const menuGroups: AppShellMenuGroup[] = [
  {
    items: [
      { key: 'a', label: '订阅管理', icon: MENU_ICONS.subscriptions },
      { key: 'b', label: '录制状态', icon: MENU_ICONS.recordings },
    ],
  },
  {
    group: '训练',
    items: [{ key: 'c', label: '数据集', icon: MENU_ICONS['training-datasets'] }],
  },
]

describe('AppShell', () => {
  it('should render the brand, every menu item and the group title', () => {
    render(
      <AppShell menuGroups={menuGroups} currentPage="a" onSelect={vi.fn()}>
        <div>内容</div>
      </AppShell>,
    )

    expect(screen.getByText('Z.Vtuber 管理后台')).toBeInTheDocument()
    expect(screen.getByText('订阅管理')).toBeInTheDocument()
    expect(screen.getByText('录制状态')).toBeInTheDocument()
    expect(screen.getByText('训练')).toBeInTheDocument()
    expect(screen.getByText('数据集')).toBeInTheDocument()
    expect(screen.getByText('内容')).toBeInTheDocument()
  })

  it('should mark the current page as selected', () => {
    render(
      <AppShell menuGroups={menuGroups} currentPage="b" onSelect={vi.fn()}>
        <div>内容</div>
      </AppShell>,
    )

    expect(screen.getByText('录制状态').closest('li')).toHaveClass('ant-menu-item-selected')
    expect(screen.getByText('订阅管理').closest('li')).not.toHaveClass(
      'ant-menu-item-selected',
    )
  })

  it('should call onSelect with the clicked menu key', () => {
    const onSelect = vi.fn()
    render(
      <AppShell menuGroups={menuGroups} currentPage="a" onSelect={onSelect}>
        <div>内容</div>
      </AppShell>,
    )

    fireEvent.click(screen.getByText('数据集'))

    expect(onSelect).toHaveBeenCalledWith('c')
  })
})
