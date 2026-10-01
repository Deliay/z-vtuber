import { theme, type ThemeConfig } from 'antd'

/**
 * 全站唯一的设计语言入口（暗色 Design Token）。
 *
 * - 以 antd `darkAlgorithm` 为基底，叠加 `compactAlgorithm` 提升后台信息密度；
 * - Seed Token 收敛品牌色（青 `#38bdf8`）、圆角、字号与控件高度；
 * - 对象保持稳定引用（模块级常量），避免每次渲染重建主题导致 antd 失效。
 *
 * 约定：业务代码不得再自行定义颜色/圆角/间距常量（内联 style 亦禁止），
 * 统一从 antd token / 组件 props 取值。
 */
export const appTheme: ThemeConfig = {
  algorithm: [theme.darkAlgorithm, theme.compactAlgorithm],
  token: {
    colorPrimary: '#38bdf8',
    colorInfo: '#38bdf8',
    colorSuccess: '#22c55e',
    colorWarning: '#eab308',
    colorError: '#f87171',
    colorBgLayout: '#0f172a',
    colorBgContainer: '#1e293b',
    colorBgElevated: '#1e293b',
    colorBorder: '#334155',
    colorBorderSecondary: '#1e293b',
    colorText: '#e2e8f0',
    colorTextSecondary: '#94a3b8',
    colorTextTertiary: '#64748b',
    borderRadius: 6,
    fontSize: 14,
    controlHeight: 32,
    fontFamily:
      "system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'PingFang SC', 'Microsoft YaHei', sans-serif",
  },
}
