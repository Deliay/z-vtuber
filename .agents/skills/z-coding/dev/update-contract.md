# 更新Contract

默认目录: contracts/

读取 `<产品文档>`,  `docs/engineering/frontend-rules.md`, `docs/engineering/backend-rules.md`和 `contracts/`目录下的结构。

使用<SKILL: z-git> （如果没有z-git这个skill，可以无视分支管理这部分，或者遵循其他分支管理的skill规范）或其他skill中的git规范，分支前缀的`<type>`使用`contract`，分支基于`<feat-branch>`（可在`proposal`中找到这个branch），在上生成对应的 contract schema。并提交，如果有`pull request`的工具，则提起`pull request`让用户审批。此时中断Agent循环，让用户输入是否审批完成。

如果用户显示告知review通过，则操作这个`pull request`合并到`<feat-branch>`
