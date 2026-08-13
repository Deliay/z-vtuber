---
name: z-coding
description: 'USE FOR: development, dev, write code, read code, write tech proposal documents, api test, e2e test, implemention, implement product, debug, fix issue, troubleshooting, development workflow. 开发，写代码，读取代码，技术文档，技术方案，写测试，更新测试，实现产品逻辑，写技术方案，开发流程，开发规范'
---

# 开发流程

你需要严格按照契约式来进行开发，例如 `openapi-spec`, `graphql-schema` 放置在 `contract/` 中。

## 优先使用SKILL

在你不确定的时候，优先从已经有的SKILL中检索知识。有SKILL则使用SKILL。

## 文档标准

编写文档使用一个统一标准的格式，如：<SKILL: z-document>。

## 阅读产品文档

每次开发前需要指定一个产品文档，默认可以用`ask`或者`question`工具从目录`docs/product/reviewed`列出日期最新的几个文档，让用户选择或输入`<产品文档>`。

## 开发流程

创建需求分支 -> 编写技术方案 -> 更新Contract -> 创建开发分支 -> 编写实现[1] -> 编排服务 -> 覆盖API测试 -> 覆盖e2e测试。

[1] 编写实现部分是并行进行subagent调用。

为这套流程创建`todo`。

### 加载规范

在写代码之前，如果上下文没有加载对应目录下的AGENTS.md，则进行加载。

例如：修改 `backend/src/x/y/z/a.ts`，加载 `backend/AGENTS.md`

每次进行代码编写，需要加载`docs/engineering/common-rules.md`

### 创建需求分支

在指定了需要开发的产品文档后，在远端创建开发分支`<feat-branch>`，分支名是`feat/<slug>`，其中`<slug>`产品文档内容的提炼。

### 编写技术方案

USE FOR: write proposal, update proposal
REFERENCE: [[dev/dev-proposal-template.md]]

### 更新Contract

USE FOR: write contract, update contract
REFERENCE: [[dev/update-contract.md]]

### 编写实现

USE FOR: implemention, write code, coding
REFERENCE: [[dev/write-implementation.md]]

### 编排服务

默认使用 <SKILL: z-aspire-orchestrator>， 如果没有则使用docker-compose+本地启动进行编排。

## 编写集成 API test & e2e test

默认使用 <SKILL: z-test>， 如果则自行决定如何覆盖。
