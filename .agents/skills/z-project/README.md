# Z-Project

项目创建与管理技能。

## 概述

Z-Project 提供标准化的项目创建与管理流程，适用于单项目和多服务 monorepo 场景。

## 核心流程

业务建模 -> 技术栈选型 -> 创建项目结构 -> 开发规范 -> 初始化各个项目 -> 服务编排

## 项目结构

### 单项目

```
docs/
docs/product/      - 产品文档
docs/engineering/  - 开发规范
```

### Monorepo

```
servers/           - 后端项目
apps/              - 前端项目
packages/          - 公共包
docs/              - 文档
docs/product/      - 产品文档（draft/reviewed/shipping/released）
docs/engineering/  - 开发规范
contracts/         - 契约文件（OpenAPI, GraphQL 等）
tests/             - 集成测试、E2E 测试
```

详细结构规范见 [[mono-repo-project-structure.md]]。

## 开发规范

- `docs/engineering/common-rules.md` - 公共规范（开发流程、分支规范、Commit 规范）
- `docs/engineering/frontend-rules.md` - 前端规范
- `docs/engineering/backend-rules.md` - 后端规范

## 使用说明

当用户需要创建或管理项目时，加载此技能获取详细工作流程指引。

## 相关文档

- [[project-structure.md]]
- [[mono-repo-project-structure.md]]
- [[SKILL.md]]