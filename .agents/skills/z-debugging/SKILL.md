---
name: z-debugging
description: 'USE FOR: debug, debugging, test, api test, e2e test, test environment'
---
# 调试

## 诊断原则

1. 不要看代码猜测
2. 用e2e test/api test复现出实际问题
3. 勤看日志：查看浏览器的debug console和使用`aspire logs`(<SKILL: z-aspire-orchestrator>)或`docker logs`来看日志诊断问题
4. e2e要勤看截图，使用playwright的截图能力配合图片理解工具进行诊断。

## 优先使用SKILL

在你不确定的时候，优先从已经有的SKILL中检索知识。有SKILL则使用SKILL。

## 调试工作流

使用`todo`工具新建如下`todo`

问题核实 -> 环境准备 -> 复现问题 -> 修复问题

## 问题核实

在上下文不全的情况下，或者用户只抛出了一部分问题，则需要主动和用户沟通上下文信息。使用`ask`或者`question`工具。

在用户显式的说没有其他补充时，进入到下一个环节。

如果有orchestrator相关技能，如 <SKILL: z-aspire-orchestrator>，可以尝试使用，拉起项目使用e2e和图片理解能力确认问题。

## 环境准备

使用 `@general` subagent 来准备环境：使用<SKILL: z-git> （如果没有z-git这个skill，可以无视分支管理这部分，或者遵循其他分支管理的skill规范）或其他skill中的git进行分支管理，每次进行调查根据当前分支创建新的分支，`<type>`为`issues`，`<slug>`为上面核实问题的简写。在这个新的分支进行后续操作

在这个分支使用你的 orchestrator 技能 来启动相关的服务，准备完成后进入到下一个步骤

## 复现问题

按照和用户沟通的信息，使用e2e或者api测试尝试复现问题，包括但不限于可以截图查看浏览器状态等手段。

## 修复问题

使用 `@general` subagent 来进行修复：修复完成后，提`pull request`让用户进行review。
修复时，需要编写e2e test或api test防止之后再出现类似的问题。
