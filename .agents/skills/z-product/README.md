# Z-Product

产品文档编写与管理技能。

## 概述

Z-Product 提供标准化的产品文档创建、修改与发布流程，支持产品生命周期的全阶段管理。

## 文档生命周期

| 阶段 | 目录 | 说明 |
|------|------|------|
| 草稿 | `docs/product/draft` | 新建的产品文档 |
| 已审核 | `docs/product/reviewed` | 通过 Review 的文档 |
| 开发中 | `docs/product/shipping` | 开始实现的文档 |
| 已上线 | `docs/product/released` | 成功上线验收的文档 |

## 核心流程

### 创建产品文档

使用设问模式与用户沟通产品细节，基于主分支创建新的分支，新建文档放入 `docs/product/draft`，提交 PR 待用户 review。

详见 [[prd/create-prd.md]]

### 修改产品文档

根据 PR 序号或文档名称检出对应分支，分析 Review Comment 并修复，完成后提交推送。

详见 [[prd/edit-prd.md]]

### 发布产品文档

将审核通过的文档从 `draft` 移动到 `reviewed` 目录。

详见 [[prd/reviewed-prd.md]]

## 文档模板

使用 [[prd/product-document-template.md]] 模板创建产品文档，包含：
- 背景与目标
- 用户分析
- 功能需求
- 非功能需求
- 依赖与约束

## 注意事项

- 产品文档中**禁止提及技术细节**
- 不涉及项目结构、前端组件、后端 API 等技术内容
- 文档应图文并茂，推荐使用 Mermaid 图表

## 相关文档

- [[SKILL.md]]
- [[prd/create-prd.md]]
- [[prd/edit-prd.md]]
- [[prd/product-document-template.md]]
- [[prd/reviewed-prd.md]]