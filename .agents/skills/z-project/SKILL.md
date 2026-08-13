---
name: z-project
description: 'USE FOR: project manage, create project, new project, read project, 项目管理，创建项目，读取项目'
---

# Create Project

创建项目遵照如下流程：
业务建模 -> 技术栈选型 -> 创建项目结构 -> 开发规范 -> 初始化各个项目 -> 服务编排

## 优先使用SKILL

在你不确定的时候，优先从已经有的SKILL中检索知识。有SKILL则使用SKILL。

## 文档标准

编写文档使用一个统一标准的格式，如：<SKILL: z-document>。

## 项目服务建模

在开始前，如果用户没有简短的介绍这个项目主要核心领域是什么，则终止agent循环，向用户直接提问，让用户说明这个项目主要核心领域或主要的业务是什么。

## 技术栈选型

根据用户输入的核心领域信息，使用`ask`或`question`工具动态地和用户交互并询问技术选型，提供主流技术栈选项，如果在提示词中显式指定了语言，则默认选项是这个语言最常用的几个技术栈。

分门别类的每个询问技术栈，分类为：前端、后端、持久化、缓存、其他组件等。

## 项目结构

在初始化前检查是否有初始化项目的git仓库，如果没有，执行

```bash
git init
git branch -m main
```

按照用户输入的技术栈选型为用户创建项目结构的目录
REFERENCE: [[project-structure.md]]

## 开发规范

项目初始化完成后，在文档`docs/engineering/`文件夹中生成相关技术栈的统一规范，并在各个项目中的`AGENTS.md`引用这些规范。

AGENTS.md中需要显示的注明，要求agent要有SKILL就用SKILL，并附上如下SKILL引用说明：

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

引用关系例如：
```
docs/engineering/common-rules.md
docs/engineering/frontend-rules.md
docs/engineering/backend-rules.md
```

`common-rules.md`包含：开发流程，如TDD开发，契约式开发，开发前加载产品文档，覆盖率要求，review要求等），分支规范，commit规范等，需要根据具体的技术栈展开详细内容(这一部分只包含公共的规范)。

其中`common-rules.md`应该包含如下skill的说明
```
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
```

`frontend-rules.md`包含：前端统一的规范，初始化指令，项目选型，项目结构，组件命名，状态管理选型，打包器，测试框架，contract中的契约生成器(codegen)等规范。除了特殊指定，否则都使用这个文件里的选型。需要按照具体的技术栈选型展开写入详细内容。

`backend-rules.md`包含：后端的统一规范，初始化指令，项目选型，项目结构，测试框架，contract中的契约生成器(codegen)等规范。除了特殊指定，否则都使用这个文件里的选型。需要按照具体的技术栈选型展开写入详细内容。



## 初始化各个项目

按照技术栈选型在对应的项目结构的目录中初始化项目

优先使用技术栈官方提供的cli/脚手架创建，如果没有再由大模型来生成项目。

这里需要注意官方提供的脚手架是否会创建子目录，如果会的话使用在当前目录直接创建的命令。

如：

```bash
# cwd: apps/my-front-end-app

# 错误！文件夹结构变成了 apps/my-front-end-app/my-front-end-app
npx create-react-app --template typescript my-front-end-app

# 正确，文件夹结构依然是 apps/my-front-end-app
npx create-react-app --template typescript .
```

然后安装相关的依赖，这些依赖在前置的选型中有表明。

如果用户在前置的`ask`,`question`环节输入了一下业务的概述，不要为这些概述生成任何代码。只搭建项目框架。

初始化完成之后，在对应项目的目录生成这个项目的技术栈简单说明，写入项目的`AGENTS.md`中。

其中内容包括：
```
# <项目名称，如my-front-end-app>
## 技术栈说明
## 项目模块示意图
## 开发流程
## 相关规范引用
```

单个项目初始化完成之后进行一次commit。
