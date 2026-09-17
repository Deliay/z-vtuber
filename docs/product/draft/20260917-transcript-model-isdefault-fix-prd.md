---
description: 修复转写服务因 transcript_models 集合存量 IsDefault 字段导致 MongoDB 反序列化失败、任务列表等接口 500 的问题
type: Permanent
---

# Transcript 模型存量字段兼容修复

**功能名称**: Transcript 模型存量字段兼容修复
**PRD 版本**: v1.0
**创建日期**: 2026-09-17
**作者**: AI Agent

## 背景与目标

### 1.1 背景

「Transcript 识别优化（显式模型 + 识别上下文）」（[[20260917-transcript-model-context-prd]]）移除了"默认模型"概念，代码侧删除了 `TranscriptModelRecord.IsDefault` 字段、`GetDefaultAsync` 方法与 `IsDefault_1` 索引。但线上 `transcript_models` 集合中的存量文档是在旧版本写入的，仍携带 `IsDefault` 元素。

MongoDB C# 驱动在反序列化时，默认对类中不存在对应映射的文档元素抛出 `FormatException`：

```
System.FormatException: Element 'IsDefault' does not match any field or property of class
Z.Vtuber.Common.Persistence.Entities.TranscriptModelRecord.
   at Z.Vtuber.Common.Persistence.TranscriptModelRepository.ListAsync(...)
   at Z.Vtuber.RecordingTranscript.Api.Controllers.TranscriptTasksController.MapTasksAsync(...)
   at Z.Vtuber.RecordingTranscript.Api.Controllers.TranscriptTasksController.ListTasks(...)
```

导致 recording-transcript-api 的任务列表接口（`GET /api/transcripts/task`）持续 500；任何读取模型列表的链路（模型管理、设置列表、任务详情等）同样受影响。

更深层的问题是：**代码删除了字段，但没有任何一方负责清理存量数据**——既有的 `TranscriptModelMigration` 只回填新增的 `Model` 字段，且不清理已删除的 `IsDefault`；该迁移也仅在 consumer 服务启动时执行，api 服务自身没有迁移路径。

### 1.2 目标

1. **止血**：recording-transcript-api 所有读取 `transcript_models` 的接口恢复正常，存量文档中的多余字段不再导致 500。
2. **清源**：存量文档中的 `IsDefault` 字段被彻底清理，数据库内容与实体定义恢复一致。
3. **防再发**：未来实体字段被删除/重命名时，同类"代码先行、数据残留"问题不再引发线上故障。

### 1.3 成功标准

- 线上 `GET /api/transcripts/task`、`GET /api/transcripts/models`、`GET /api/transcripts/settings` 等接口恢复 200，不再出现 `FormatException`；
- `transcript_models` 集合中不再存在 `IsDefault` 元素；
- 修复部署不依赖服务启动顺序（api 单独先发布也能恢复）。

## 用户分析

### 2.1 目标用户

- 站内运营/管理员：查看转写任务列表、管理识别模型，当前相关页面不可用。

### 2.2 用户场景

| 场景 | 用户角色 | 目标 | 痛点 |
|--|--|--|--|
| 查看转写任务列表 | 运营 | 看到各场次转写任务状态 | 接口 500，页面报错无数据 |
| 管理识别模型 | 运营 | 增删改识别模型 | 模型列表读取失败，管理入口不可用 |

## 功能需求

### 3.1 功能概述

从两个层面修复：实体反序列化对未知字段容错（立即恢复接口），以及对存量文档执行幂等清理迁移（消除脏数据），两者互补且不依赖部署顺序。

### 3.2 功能列表

#### 功能点 1: 实体反序列化容错（止血）

- **描述**: `TranscriptModelRecord` 实体标注忽略未映射的 BSON 元素，存量文档中残留的 `IsDefault`（以及未来可能出现的其他历史字段）不再导致反序列化异常；读取到的记录按现有字段正常映射。
- **用户价值**: 接口立即恢复可用，不等待数据清理完成。
- **验收标准**:
  - [ ] 集合中存在含 `IsDefault` 元素的存量文档时，模型列表/任务列表等读取接口正常返回 200
  - [ ] 读取结果中各字段值正确，不受多余元素影响

#### 功能点 2: 存量字段清理迁移（清源）

- **描述**: 提供幂等迁移，对 `transcript_models` 集合中仍含 `IsDefault` 元素的文档执行 `$unset` 清理；迁移可重复执行、可随服务启动自动完成，沿用既有 `zvtuber_meta` marker 模式避免重复劳动。
- **用户价值**: 数据库内容与实体定义一致，不留技术债；后续审计与维护不被残留字段干扰。
- **验收标准**:
  - [ ] 迁移执行后集合中不存在 `IsDefault` 元素
  - [ ] 迁移幂等：重复执行不产生副作用、不报错
  - [ ] 不含 `IsDefault` 的文档不被改动

#### 功能点 3: 回归防护

- **描述**: 以测试固化"存量含未知字段的文档可被正常读取"的行为，防止未来删除实体字段时再次引入同类故障。
- **用户价值**: 同类问题在开发阶段即被拦截，不再流到线上。
- **验收标准**:
  - [ ] 存在覆盖"文档含未映射字段 → 反序列化成功"的自动化测试
  - [ ] 存在覆盖清理迁移行为（清理生效 + 幂等）的自动化测试

### 3.3 用户操作流程

本需求为服务端缺陷修复，无用户操作流程变化；修复上线后运营按既有方式使用任务列表与模型管理功能。

### 3.4 页面/界面描述

无界面变更。修复后 web 端转写任务列表、模型管理页面恢复既有展示。

### 3.5 异常与边界情况

| 情况 | 预期行为 |
|------|---------|
| 文档同时含 `IsDefault` 与其他未知字段 | 反序列化正常，未知字段一律忽略 |
| 迁移执行时集合为空 | 正常完成，不报错 |
| 迁移中断后服务重启 | 重新执行，幂等无副作用 |
| api 先于 consumer 部署发布 | api 接口已恢复（功能点 1），不依赖 consumer 是否已运行 |

## 四、非功能需求

### 4.1 性能要求

迁移为一次性批量 `$unset`，对线上读写无感知；接口路径无任何额外开销。

### 4.2 安全性要求

无新增对外接口、无数据外泄面；迁移仅删除字段不改动业务数据。

### 4.3 兼容性要求

- 不改任何 API 契约与响应结构；
- 存量转写任务、segments、设置数据不受影响；
- 修复可独立发布、可直接回滚（回滚后旧代码含 `IsDefault` 字段映射，已清理的数据依然兼容）。

## 五、依赖与约束

### 5.1 依赖

- 无外部依赖；使用既有 MongoDB 迁移设施（`zvtuber_meta` marker 幂等模式）。

### 5.2 约束

- 修复不得影响正在运行的转写任务与识别流程；
- 迁移实现须与现有回填/迁移代码风格一致（幂等、可重入、有日志）。

## 六、相关文档

- 技术提案：`docs/engineering/proposals/20260917-proposal-transcript-model-isdefault-fix.md`
- 引入问题的需求：[[20260917-transcript-model-context-prd]]（删除 `IsDefault` 字段的来源）
- 转写功能 PRD：[[20260916-recording-transcript-prd]]
