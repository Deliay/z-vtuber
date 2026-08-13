# 开发规范

本文档是 Z.Vtuber 项目的统一开发规范（公共部分）。

## 核心原则

- **TDD 开发**：先编写测试，再实现代码，确保每个功能都有对应的测试覆盖。
- **契约式开发**：接口先定义契约（OpenAPI），前后端基于契约进行联调。
- **开发前加载产品文档**：动手前先阅读 `docs/product/` 下的产品文档，理解业务上下文。
- **覆盖率要求**：核心逻辑单元测试覆盖率不低于 80%。
- **Review 要求**：所有合并到主分支的代码必须经过至少一次 review。

## Skill 引用

要求 agent 有 SKILL 就用 SKILL，本项目引用如下 SKILL：

| Skill | Description |
|------|------|
| z-product | 产品文档创建与生命周期管理 (PRD 工作流) |
| z-project | 项目创建与管理 (monorepo 支持、技术栈选择) |
| z-coding | 契约式开发工作流 (提案、契约、实现) |
| z-git | Git 工作流管理 |
| z-document | 文档规范 (Zettelkasten 方法、Mermaid 图表) |
| z-aspire-orchestrator | 使用 Microsoft Aspire 进行服务编排 |
| z-test | API 测试和 E2E 测试工作流 |
| z-debugging | 调试方法论与故障排除 |

## 分支规范

- `main`：主干分支，始终保持可发布状态。
- 功能分支命名：`feature/<name>`
- 修复分支命名：`fix/<name>`
- 基础设施分支：`infra/<name>`

## Commit 规范

使用 Conventional Commits 格式：

- `feat:` 新功能
- `fix:` 修复缺陷
- `docs:` 文档变更
- `chore:` 构建/辅助工具变更
- `refactor:` 重构
- `test:` 测试变更

## 文档引用

- 公共规范：`docs/engineering/common-rules.md`
- 前端规范：`docs/engineering/frontend-rules.md`
- 后端规范：`docs/engineering/backend-rules.md`
