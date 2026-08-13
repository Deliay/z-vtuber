---
name: z-git
description: 'USE FOR: source code management, SCM, git, commit'
---

# Git操作规范

## 禁止change base

禁止使用rebase / squash 等操作，只允许merge

## 优先使用SKILL

在你不确定的时候，优先从已经有的SKILL中检索知识。有SKILL则使用SKILL。

## 分支不要复用

在没有显式标记当前会话主题结束之前，如果工作区非clean状态，则提示当前工作区不干净。举例来说，用户在开发了需求A后，没有提交，还有需求A的改动，此时要求开发需求B，你应该中断Agent循环，并提示工作区不干净。

## 分支命名规范

分支命名为：`<type>/<slug>`

其中`<type>`为

- prd: 产品文档分支
- feat: 需求开发分支
- 外部指定的其他type

`<slug>`为上下文中确定的分支slug

## Pull Request规范

在合并pull request后，需要删除远端和本地的分支，注意**不要**删除`pull request`的目标分支。
