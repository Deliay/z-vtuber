# z-coding

开发流程技能库，定义契约式开发的工作流程。

## 目录结构

```
z-coding/
├── SKILL.md                      # 主技能定义
└── dev/                         # 开发流程文档
    ├── write-proposal.md        # 编写技术方案
    ├── update-contract.md       # 更新 Contract
    ├── write-implementation.md  # 编写实现
    └── dev-proposal-template.md # 技术提案模板
```

## 开发流程

1. **阅读产品文档** - 从 `docs/product/reviewed` 选择最新的产品文档
2. **创建需求分支** - 基于产品文档创建 `feat/<slug>` 分支
3. **编写技术方案** - 编写提案并提交 PR 进行 review
4. **更新 Contract** - 定义 API 契约并提交 PR 进行 review
5. **创建开发分支** - 基于 `feat-branch` 创建开发分支
6. **编写实现** - 并行委托 subagent 实现各子项目
7. **编排服务** - 使用 `z-aspire-orchestrator` 或 docker-compose
8. **覆盖 API 测试 & E2E 测试** - 使用 `z-test` 覆盖测试

## 核心规范

- 契约式开发：API 契约放置在 `contracts/` 目录
- 代码编写前加载 `docs/engineering/common-rules.md`
- 遵循 `frontend-rules.md` / `backend-rules.md` 规范
