---
description: 管理后台 UI 重设计实现 Plan：引入 Ant Design v6（antd@^6.6 + @ant-design/icons），以暗色 Design Token（theme.ts + ConfigProvider/App/useApp）落地统一设计语言，AppShell 布局骨架 + PageHeader，逐页以 Table/Form/Modal/Tabs/Empty/Spin/Tag/message 替换 268 处内联样式与手工 UI 原语，Modal.confirm 统一危险操作（替换 5 处 window.confirm 与 2 处行内两步确认），P1–P4 子分支分批实施，功能/接口/文案零变更、既有测试语义断言不削弱
type: Permanent
---

# 管理后台 UI 重设计 - 实现 Plan

**功能名称**: 管理后台 UI 重设计（全站视觉与交互升级）
**关联 PRD**: `docs/product/draft/20261001-admin-console-ui-redesign-prd.md`（v1.0）
**关联技术提案**: `docs/engineering/proposals/20261001-proposal-admin-console-ui-redesign.md`（v1.0）
**Plan 版本**: v1.0
**创建日期**: 2026-10-01
**作者**: Workflow Agent（代起草）
**需求总分支**: `feat/admin-console-ui-redesign`（需求总 PR：https://github.com/Deliay/z-vtuber/pull/165，base=main）

## 1. 概述

按技术提案在 `apps/web` 引入 Ant Design v6，以暗色 Design Token 建立统一设计语言，替换全部手工 UI 原语，重做六大模块页面的视觉与交互。**功能、接口、数据口径、操作路径与文案零变更**，既有自动化测试语义断言保持通过。实施遵循 `docs/engineering/common-rules.md`（TDD、契约式开发、覆盖率 ≥ 80%、Conventional Commits）、`docs/engineering/frontend-rules.md`、`apps/web/AGENTS.md` 与 z-coding 流程；范围外的 `servers/*/AGENTS.md` 对应服务零改动，不涉及其规范。

### 1.0 现状核实结论（2026-10-01 读码复核，与提案一致）

- 无样式体系：`src/index.css` 7 行（仅 `color-scheme: dark` + reset）、`src/App.css` 73 行硬编码 slate 色布局；`style={{...}}` 全站 **268 处、17 个组件**（重灾区 `transcripts/SettingsTab.tsx` 45、`transcripts/TaskListTab.tsx` 44、`cookie/CookiePage.tsx` 40）；`inputStyle` 等 JS 样式常量多文件重复。
- 确认交互三种并存：`window.confirm` **5 处**（`transcripts/SettingsTab.tsx:132`、`training/DatasetSelector.tsx:108`、`training/DatasetItemList.tsx:260`、`training/ModelSettingsTab.tsx:111,169`）+ 行内两步确认 **2 处**（`notification/NotificationPage.tsx`「确认删除」、`subscription/SubscriptionPage.tsx`「确认取消」）+ 手写弹窗。
- 无 Toast（散落彩色 `<p>`）、无 spinner（「加载中...」文本）、无 Empty/Alert 三态；弹窗无焦点管理。
- 测试全以 RTL role/text 查询；结构断言集中在 `training/DatasetItemList.test.tsx:196`（`colgroup col`）、`:367-385,403-404`（`closest('tr')`/`colspan`）；测试钩子 `data-testid`（`waveform-container`、App/TranscriptsPage 的 mock 页）须保留。
- `apps/web/package.json` 仅 `react@19` / `react-dom@19` / `wavesurfer.js@8`，无样式方案冲突；测试 16 个页面级 `*.test.tsx` + `App.test.tsx` + `api/client.test.ts` 等共约 20 个。

### 1.1 实施范围

- **做**：`apps/web` 全部 UI 层——`src/App.tsx`、`src/App.css`、`src/index.css`、`src/pages/**`（18 个非测试组件）、新增 `src/theme.ts` 与 `src/components/`（`AppShell.tsx`、`PageHeader.tsx`）；`apps/web/package.json`（+`antd@^6.6`、`@ant-design/icons@^6`）；测试文件同步适配并补 UI 体系用例；`apps/web/AGENTS.md` 与 `docs/engineering/frontend-rules.md` 补 UI 规范。
- **不做**：`contracts/*.yaml`、`servers/**`、`tests/Z.Vtuber.Tests`（dotnet）零改动；`src/api/client.ts` 封装签名、URL 构造器、`apiErrorMessage`/`apiErrorCodeText` 零改动；不做 URL 路由化、登录鉴权、移动端适配、明暗切换；复杂交互（SSE/波形/试听/扫码/轮询）业务逻辑零改动，仅换壳。

### 1.2 行为回归基准（零变更红线）

以下语义逐条保持，作为每阶段回归对照（出处见 `apps/web/AGENTS.md`）：

- 表格语义：`DatasetItemList` 内联编辑 payload（含 `endMs: null` 显式序列化、`m:ss`/`m:ss.S` ↔ 毫秒、非法时间不保存）、多选批量删除、分组切换与分组头全选联动、「展示类型」列显隐（音频信息六列默认隐藏 / 情绪信息十列默认展示 / 文本与操作恒展示）、行下插展开行且同一时刻仅展开一行。
- 复杂交互：`SegmentAudioPlayer` 共享单例 `<audio>`（同句停止/他句切源/ended 清高亮/失败「音频暂不可用」非阻断/负偏移钳 0）；`DatasetItemWaveform` 加载即自动播放、destroy 不串播、`endMs` 空取 `startMs+300000` 试听上限标注、失败降级不阻断；Transcripts SSE（`EventSource` 追加 + 断开重连 + 底部跟随 + 「收起」）、播放偏移保存流程（`Math.round(seconds*1000)`、失败「偏移无效」、400 保持原值、成功重建 SSE）；Cookie 扫码 QR 轮询与自动关闭；`RecordingStatusPage` 周期轮询；`EmotionTaskQueue` 10s 轮询与请求序号比对防慢响应覆盖；弹窗「导入/添加进行中忽略遮罩/禁用关闭」防重复提交。
- 数据与文案：全部中文文案、错误码映射（`apiErrorMessage`/`apiErrorCodeText`）与数据口径不变；`apiKey` 永不回显明文；`aria-label`（如 `aria-label="文本"`）与可访问名不变。
- 防重复提交：保存/批量删除/添加/重试/识别等进行中禁用语义保持（改由 `Button loading` 承载）。

## 2. 分支与提交流程

- 需求总分支 `feat/admin-console-ui-redesign`；四个子分支自需求总分支切出，完成后向需求总分支提 PR，验收后经需求总 PR #165 合入 `main`。
- 子分支：`feat/admin-ui-redesign-foundation`（P1）→ `feat/admin-ui-redesign-feedback`（P2）→ `feat/admin-ui-redesign-pages`（P3）→ `feat/admin-ui-redesign-cleanup`（P4）。
- 提交顺序与类型（Conventional Commits，测试与实现分开）：
  1. `feat(web):` 依赖 + `theme.ts` + 壳层（P1）
  2. `feat(web):` 反馈与确认体系（P2）
  3. `feat(web):` 页面迁移（P3，可按页面拆多个提交）
  4. `test(web):` 测试适配与新增用例（随各阶段或独立提交）
  5. `docs(web):` 内联样式清理说明 + `apps/web/AGENTS.md` / `frontend-rules.md` 同步（P4）
- 每个子分支合入需求总分支前必过：`npm run lint` / `npm run test` / `npm run build` 全绿（在 `apps/web` 下执行）。

## 3. 实施阶段

### Phase 0: 技术文档（已先行）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 0.1 | 产品文档 + 技术提案（已完成，提交 `973c7c9`） | `docs/product/draft/20261001-admin-console-ui-redesign-prd.md`、`docs/engineering/proposals/20261001-proposal-admin-console-ui-redesign.md` | 与本 Plan 口径一致 |
| 0.2 | 实现 Plan（本文档） | `docs/engineering/plans/20261001-plan-admin-console-ui-redesign.md` | 任务/文件/验收明确，与提案互链 |

### Phase 1（P1 基建，`feat/admin-ui-redesign-foundation`）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 1.1 | 引入依赖 `antd@^6.6`、`@ant-design/icons@^6`（锁 ^6，勿升 v7） | `apps/web/package.json` | `npm run build` 通过，React 19 兼容 |
| 1.2 | TDD：新增 `AppShell` 菜单切换/分组渲染/默认页用例先行 | `apps/web/src/App.test.tsx`、`src/components/AppShell.test.tsx` | 用例先红后绿；既有 App 断言不削弱 |
| 1.3 | 新增 `src/theme.ts`：`algorithm: theme.darkAlgorithm`（可叠加 `compactAlgorithm` 提密度）+ Seed Token（品牌青 `#38bdf8` 系、`borderRadius`、`fontSize`、`controlHeight`）；`theme` 对象保持稳定引用 | `apps/web/src/theme.ts` | 深色对比度人工校验：正文 ≥ 4.5:1、非文本控件 ≥ 3:1 |
| 1.4 | `App.tsx` 顶层 `ConfigProvider`（`locale={zhCN}` + `theme`）包裹 antd `App`；页面内反馈一律 `App.useApp()`（禁用静态 `message.xxx`/`Modal.confirm`） | `apps/web/src/App.tsx` | 中文 locale 与主题生效；`App.test.tsx` 菜单文案/切换断言保持 |
| 1.5 | 新增 `src/components/AppShell.tsx`：antd `Layout` + `Layout.Sider` + `Menu` 承载既有 `menuGroups`/`PageKey` 结构（「训练」独立分组、默认「订阅管理」、当前项高亮）；新增 `PageHeader.tsx`（标题 + 一句话说明 + 主操作区） | `src/components/AppShell.tsx`、`PageHeader.tsx` | 菜单项与分组与现状一致；窄窗口不破版 |
| 1.6 | `App.css` 手工布局规则删除（仅留最小 reset），`index.css` 收敛为最小全局样式 | `src/App.css`、`src/index.css` | 壳层渲染与切换可用，lint/test/build 全绿 |

### Phase 2（P2 反馈体系，`feat/admin-ui-redesign-feedback`）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 2.1 | 写操作反馈统一 `message.success`/`message.error`（`useApp()`），替换散落彩色 `<p>` | 全部 `src/pages/**` | 每个写操作都有即时统一提示 |
| 2.2 | 5 处 `window.confirm` → `Modal.confirm`（标题 + 影响范围说明 + `okButtonProps: { danger: true }`）：删除数据集（`DatasetSelector.tsx:108`）、批量删除条目（`DatasetItemList.tsx:260`）、删除 Provider「其下 N 个模型将一并删除」（`ModelSettingsTab.tsx:111`）、删除模型（`ModelSettingsTab.tsx:169`）、删除自动识别设置（`SettingsTab.tsx:132`） | 各调用点 | 全站无 `window.confirm`；确认文案含影响范围 |
| 2.3 | 2 处行内两步确认 → `Modal.confirm`：删除通知绑定（`NotificationPage.tsx:160-196`）、取消订阅（`SubscriptionPage.tsx:244-284`） | `NotificationPage.tsx`、`SubscriptionPage.tsx` | 全站无行内两步确认 |
| 2.4 | 三态统一：`Spin`（区块级）/`Table loading`、`Empty`（`description` + 行动引导按钮）、`Alert` + 重试入口；提交中 `Button loading` 防重复 | 全部 `src/pages/**` | 加载/空/错误三态全站一致、局部加载不阻塞整页 |
| 2.5 | 测试：移除 `window.confirm` mock，改为点击确认弹窗按钮断言；新增三态/消息反馈用例 | 各 `*.test.tsx` | 断言语义不削弱，全绿 |

### Phase 3（P3 页面迁移，`feat/admin-ui-redesign-pages`，可按页面拆子提交）

通用替换映射（详表见提案 §5.2 实现点 3）：原生 `<button>` → `Button`（`type`/`danger`/`loading`）；原生 `<table>`/`colgroup` → `Table`（`columns.width` + `ellipsis: { showTitle }`、`rowSelection`、`expandable`、`loading`、`locale.emptyText`）；`inputStyle`+原生输入 → `Input`/`Input.TextArea`/`InputNumber`/`Select`；手写弹窗 → `Modal`；手写 Tab → `Tabs`；彩色小字/裸 span chips → `Tag`/`Badge`/`Progress`。按钮可访问名与中文 label 一律不变。

| # | 任务 | 关键文件 | 验收（该页测试全绿 + 行为回归） |
|---|------|---------|------|
| 3.1 | 订阅管理页：新增订阅表单（平台/主播标识/类型，微博仅 uid）、订阅表格（平台标识列、昵称列经 `listBackstageSubscriptions`）、取消确认 | `pages/subscription/SubscriptionPage.tsx` | 平台分流调用、文案与列不变 |
| 3.2 | Cookie 管理页：帐号表格（平台/昵称/ID/Session/Cookie 状态/时间）、新增扫码弹窗（QR + 轮询 + 失败重试 + 平台区分文案）、删除确认 | `pages/cookie/CookiePage.tsx` | 扫码轮询/自动关闭行为不变；不展示 Cookie 明文 |
| 3.3 | 通知页：绑定表格、新增绑定表单（类型下拉映射不变、存量 `cookie_invalid` 按 bilibili）、删除确认 | `pages/notification/NotificationPage.tsx` | 绑定/删除行为与类型中文名不变 |
| 3.4 | 录制状态页：三态 `Tag`、实例/场次/文件、轮询刷新、展开区（实时音频入口、历史录播列表与下载） | `pages/recording/RecordingStatusPage.tsx` | 轮询与下载链接行为不变 |
| 3.5 | Transcripts：`Tabs` 双 Tab；`TaskListTab`（任务表格、SSE 识别结果区换壳、试听控件、字幕下载、播放偏移表单、手动发起识别、场次任务与重试）；`SettingsTab`（自动识别开关/添加/删除、模型增删改启停） | `pages/transcripts/*.tsx` | SSE 重连/跟随、偏移保存流程、置灰语义不变 |
| 3.6 | 试听控件换壳：仅按钮/容器/提示换 antd 外观，单例 `<audio>` 逻辑零改动 | `pages/transcripts/SegmentAudioPlayer.tsx` | `SegmentAudioPlayer.test.tsx` 全绿 |
| 3.7 | 训练 - 数据集：`DatasetsPage` `Tabs`、`DatasetSelector`、`TranscriptImportDialog`（Modal 化，导入中禁用关闭）、`DatasetItemList` 表格迁移（见 3.8） | `pages/training/DatasetsPage.tsx`、`DatasetSelector.tsx`、`TranscriptImportDialog.tsx` | 导入反馈 `{imported, skipped}`、遮罩语义不变 |
| 3.8 | `DatasetItemList` 表格改造（最大工作量）：`columns` + `ellipsis`、`rowSelection`（分组全选联动语义对齐）、`expandable.expandedRowRender` 承载 `DatasetItemWaveform`（`expandedRowKeys` 受控、同一时刻仅一行）、「展示类型」列动态过滤、内联编辑保留「行内草稿 + 保存/取消」且 payload 一致 | `pages/training/DatasetItemList.tsx` | `DatasetItemList.test.tsx` 行为断言不削弱（结构断言按 antd DOM 适配） |
| 3.9 | 训练 - 模型设置：Providers/Models 双表、`ProviderDialog`（apiKey 留空不改、永不回显）、「获取模型」导入（进行中禁用关闭）、手动添加（Provider 下拉） | `pages/training/ModelSettingsTab.tsx`、`ProviderDialog.tsx` | `ModelSettingsTab.test.tsx` 全绿；空态引导文案不变 |
| 3.10 | 训练 - 识别与情绪：`ReRecognizeDialog`（识别回填/保存同 payload/取消零请求）、`EmotionSubmitDialog`（model 必选）、`EmotionTaskQueue`（chips→`Tag`、任务表格、重试防重复、10s 轮询序号比对） | `pages/training/ReRecognizeDialog.tsx`、`EmotionSubmitDialog.tsx`、`EmotionTaskQueue.tsx` | 各自测试全绿；轮询与防重复语义不变 |
| 3.11 | 波形换壳：`DatasetItemWaveform` + `waveformLoader.ts` 零逻辑改动，仅外观与提示 | `pages/training/DatasetItemWaveform.tsx` | `waveformLoader.test.ts`、`DatasetItemWaveform.test.tsx` 全绿；`data-testid="waveform-container"` 保留 |

### Phase 4（P4 收尾，`feat/admin-ui-redesign-cleanup`）

| # | 任务 | 关键文件 | 验收 |
|---|------|---------|------|
| 4.1 | 删除 268 处内联 `style` 与 `inputStyle`/`cellStyle`/`overlayStyle`/`TRUNCATED_CELL_STYLE` 等重复常量；滚动容器样式收敛到类或 `Flex` | 全部 `src/pages/**` | 全站无「野生」样式，grep `style={{` 归零（受控组件必要 style 除外并注明） |
| 4.2 | 可访问性打磨：键盘可达、焦点可见、弹窗焦点进入/归还、状态不只靠颜色（配文字/图标）、Tooltip 对键盘可达 | 全部 `src/pages/**` | PRD 功能点 7 逐条勾验 |
| 4.3 | 新增回归用例：AppShell 菜单切换、确认弹窗、三态、消息反馈；全量测试迁移复核 | `*.test.tsx` | `npm run lint`/`test`/`build` 全绿，断言不削弱 |
| 4.4 | 文档同步：`apps/web/AGENTS.md`（目录结构 + UI 规范：统一 antd、禁止新增内联 style、主题入口 `theme.ts`、反馈走 `useApp()`）、`docs/engineering/frontend-rules.md` 补 UI/样式规范章节 | 两份文档 | 与实现一致 |
| 4.5 | 视觉走查：对照 PRD 功能点 1–7 逐页验收（对比度、层级、截断、焦点态），1280px+ 与窄窗口检查 | 全站 | PRD 验收标准逐条通过 |

## 4. 并行策略

- P1 串行先行（依赖 → theme → 壳层）；P2 与 P3 可部分并行（P2 改反馈调用点、P3 改页面渲染，同一文件冲突时以 P2 先入）。
- P3 内 3.1–3.4（订阅/Cookie/通知/录制）与 3.5–3.6（Transcripts）、3.7–3.11（训练）分组并行，组内同文件任务（3.7↔3.8）串行；每页迁移 + 该页测试适配同一提交原子完成，禁止一次性跳红。
- 3.8 为关键路径（最大工作量 + 测试结构断言适配），预留独立时间；P4 全量收尾在 P3 全部合入后进行。
- 按 z-coding「编写实现」流程可按子项目/分组派发 `general` subagent 并行开发，各自基于子分支提交并跑绿测试后提 PR。

## 5. 验收对照

| PRD 功能点 / 提案目标 | 实现任务 | 验证 |
|----------------------|---------|------|
| 功能点 1 统一设计语言 | 1.3、4.1 | 4.5 视觉走查、grep 内联样式归零 |
| 功能点 2 全局布局骨架 | 1.5、1.6 | 1.2 用例、4.5 |
| 功能点 3 表格与列表统一 | 3.1–3.11 表格迁移、3.8 | 各页测试、4.5 |
| 功能点 4 表单与操作统一 | 各页表单迁移、2.4 防重复 | 各页测试、4.5 |
| 功能点 5 反馈与确认体系统一 | 2.1–2.4 | 2.5 用例、全站无 `window.confirm` |
| 功能点 6 六大模块页面重做 | Phase 3 全部 | 既有功能逐项回归、文案/口径比对 |
| 功能点 7 可访问性 | 4.2 | 4.5、对比度校验 |
| 组件库落地（antd v6 + 暗色 Token） | 1.1、1.3、1.4 | build 全绿、`theme.ts` 唯一设计语言入口 |
| 功能/接口/文案零变更 | 全程红线（§1.2） | `contracts/*`、`servers/**`、`client.ts` diff 为零；测试中文断言不削弱 |
| lint/test/build 全绿 | 每子分支退出条件 | `npm run lint`/`test`/`build` |
| 规范文档同步 | 4.4 | AGENTS.md / frontend-rules 与实现一致 |

## 6. 风险与依赖

| 项 | 说明 | 应对 |
|---|---|------|
| 表格 DOM 变化影响测试 | `DatasetItemList.test.tsx` 的 `closest('tr')`/`colgroup`/`colspan` 结构断言需适配 antd Table | 3.8 同步适配：antd 仍输出真实 `<table>/<thead>/<th>`（`getByRole('columnheader')` 可保留），展开行断言改对 `expandedRowRender` 容器；行为断言不削弱 |
| 反馈调用不吃主题 | 静态 `message.xxx`/`Modal.confirm` 不经 ConfigProvider context | 强制 `App.useApp()`；P2 验收检查调用方式 |
| 弹窗 portal 影响查询 | RTL 默认查 `document.body` 可覆盖 | 保留 `getByRole('dialog', { name })`；`data-testid` 钩子全保留 |
| `aria-disabled` 置灰语义变化 | 改由 antd `disabled` + Tooltip 表达 | 同步适配断言（或以 Tooltip 文案断言替代） |
| antd 大版本升级面 | 锁定 `^6.6` | 升级走依赖评审；不引入 v7 |
| 性能退化 | 5000 行 UI 层迁移 + css-in-js 运行时 | 百级列表滚动走查；`expandedRowKeys` 受控防多展开；体积优化留 `zeroRuntime` 后续选项 |
| 范围蔓延 | 顺手改功能/文案 | §1.2 红线逐条回归；发现需求问题先打回上游而非自行变更 |

## 7. 相关文档

- PRD：`docs/product/draft/20261001-admin-console-ui-redesign-prd.md`
- 技术提案：`docs/engineering/proposals/20261001-proposal-admin-console-ui-redesign.md`
- 相关 PRD/提案：`20260822-admin-console-prd.md`、`20260822-proposal-admin-console.md`（管理后台初版）
- 规范：`docs/engineering/common-rules.md`、`docs/engineering/frontend-rules.md`、`apps/web/AGENTS.md`
- 流程：z-coding skill（契约式开发、subagent 并行实现）
