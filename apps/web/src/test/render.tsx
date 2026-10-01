import type { ReactElement, ReactNode } from 'react'
import { render, type RenderOptions, type RenderResult } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { appTheme } from '../theme'

/**
 * 页面测试统一渲染入口：与 `App.tsx` 一致的 `ConfigProvider`（中文 locale + 主题）
 * 与 antd `App` 上下文（`App.useApp()` 的 message/modal 依赖它），
 * 并关闭按钮文案自动插空格，保持可访问名与中文 label 一字不差。
 */
export function renderWithProviders(
  ui: ReactElement,
  options?: Omit<RenderOptions, 'wrapper'>,
): RenderResult {
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <ConfigProvider
        locale={zhCN}
        theme={appTheme}
        button={{ autoInsertSpace: false }}
      >
        <AntdApp>{children}</AntdApp>
      </ConfigProvider>
    )
  }

  return render(ui, { wrapper: Wrapper, ...options })
}
