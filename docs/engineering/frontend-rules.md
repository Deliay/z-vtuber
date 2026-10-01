# 前端开发规范

本文档描述 Z.Vtuber 项目前端（React）的统一规范。

## 技术栈

- 语言：TypeScript
- 框架：React 19
- 构建工具：Vite
- 测试框架：Vitest + React Testing Library
- 包管理器：npm

## 初始化指令

```bash
npm create vite@latest . -- --template react-ts
```

## 项目结构

```
apps/web/
├── src/
│   ├── App.tsx
│   ├── main.tsx
│   └── index.css
├── index.html
├── vite.config.ts
├── package.json
└── tsconfig.json
```

## 组件命名

- 组件文件使用 PascalCase。
- 组件目录使用小写 kebab-case。

## 状态管理

- 项目初期使用 React 内置状态管理。
- 复杂状态时引入 Zustand。

## 契约（codegen）

- 前端基于 `contracts/` 目录的 OpenAPI 规范生成 API 客户端。
- 通过 `VITE_API_URL` 环境变量配置后端地址。

## UI / 样式规范

- 组件库统一使用 **Ant Design v6**（`antd@^6.6` + `@ant-design/icons@^6`），**不升级到 v7**；全站页面一律用 antd 组件（`Table`/`Form`/`Modal`/`Tabs`/`Select`/`Tag`/`Progress`/`Typography`/`Empty`/`Spin`/`Alert` 等）承载 UI，不新增手工 UI 原语。
- 主题唯一入口为 `src/theme.ts`（暗色 Design Token），通过 `App.tsx` 的 `ConfigProvider` 注入；禁止在页面内散落颜色/间距常量，禁止绕过主题写死颜色。
- 应用外壳为 `src/components/AppShell.tsx`（布局 + 菜单）与 `PageHeader.tsx`，页面只负责内容区。
- **反馈与确认**：写操作反馈统一走 `App.useApp()` 的 `message`；危险/不可逆操作统一 `App.useApp()` 的 `modal.confirm`，禁止 `window.confirm` 与行内两步确认，禁止静态 `message.xxx`（不经 ConfigProvider context）。
- **三态统一**：区块级加载用 `Spin` 或 `Table loading`，空态用 `Empty`（带行动引导），错误用 `Alert` + 重试入口；提交中一律 `Button loading` 并禁用防重复。
- **内联样式**：禁止新增「野生」内联 `style` 常量大对象（如旧 `inputStyle`/`cellStyle`/`overlayStyle`）；仅允许 antd 布局所需的必要 `style`（如 `Form.Item`/`Space` 的 margin、`Select`/`Input` 的固定宽度），且应就近可读。样式收敛优先用 antd 属性（`size`/`block`/`Space`/`Flex`）或类。
- **可访问性**：交互控件必须有可访问名（`aria-label` 或可见文案），置灰用 `disabled`/`aria-disabled` 且与行为一致，状态不能只靠颜色（配文字/图标），弹窗焦点进入/归还由 antd 兜底。
- **测试渲染**：页面测试统一使用 `src/test/render.tsx` 的 `renderWithProviders`（中文 locale + 主题 + antd `App` 上下文），保证 `App.useApp()` 可用；`src/test/setup.ts` 已补齐 jsdom 的 `matchMedia`/`ResizeObserver`/`scrollTo`。

## 相关规范引用

- `docs/engineering/common-rules.md`
- `docs/engineering/frontend-rules.md`
