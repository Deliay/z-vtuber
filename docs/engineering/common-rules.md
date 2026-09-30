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

## 测试性能基线与超时护栏

测试是本项目的核心质量保障，**单个测试用例执行耗时不得超过 5 秒**（经评审登记在 `tests/Z.Vtuber.Tests/TimeoutExceptions.json` 的例外除外）。新增慢用例应被自动识别。

**跑测口径**（本地与提交前验证统一走这套，见「测试运行性能优化」PRD/Plan）：

```bash
# 1. 跑测试并产出 trx（逐用例耗时）
dotnet test tests/Z.Vtuber.Tests --logger "trx;LogFileName=tests.trx"

# 2. 输出耗时排行 + 5 秒超时护栏校验（>5s 未登记用例 → 非零退出）
dotnet run --project tests/Z.Vtuber.TestReport -- \
  tests/Z.Vtuber.Tests/TestResults/tests.trx \
  --check \
  --exceptions tests/Z.Vtuber.Tests/TimeoutExceptions.json \
  --top 30
```

- 报告工具 `tests/Z.Vtuber.TestReport`（仅 BCL，零新增依赖）解析 trx，按耗时降序输出用例清单并标注 `>5s`；`--check` 时对超过 5 秒且未登记的用例以非零退出码拦截。
- 护栏为**事后校验**，对测试总时长零影响。
- 例外登记：仅当用例确需真实时间流逝（如锁租约过期、调度器定时）且无法压缩到 5 秒内时，在 `TimeoutExceptions.json` 追加 `{ "test": "类全名.方法名", "reason": "理由", "since": "yyyy-MM-dd" }`；**逐条理由、数量最小化，随代码评审**。
- 慢测试根本治理：Mongo 集成测试共享夹具（`MongoDbFixture`）只在建库时初始化一次索引、用例间以 `DeleteMany` 清数据；等待异步副作用一律用 `TestWait.UntilAsync` 轮询断言，避免硬编码 `Task.Delay`。

## 文档引用

- 公共规范：`docs/engineering/common-rules.md`
- 前端规范：`docs/engineering/frontend-rules.md`
- 后端规范：`docs/engineering/backend-rules.md`
