# 编写实现

## 感知产品

读取 `<产品文档>`，`contract/`目录下的结构和对应的`proposal`文档。

### 固定知识库

如果需要进行项目理解，在explore完整体项目结构后，将这个subagent的输出创建或更新到对应项目的`AGENTS.md`。

## 使用subagent进行开发

对于前端项目：加载`docs/engineering/frontend-rules.md`
对于后端项目：加载`docs/engineering/backend-rules.md`

按照项目结构，单个`<子项目>`使用`@general` subagent，委托进行关于`proposals`文件的这个`<子项目>`部分的开发。提示词模板如下，细化之后交给agent完成：

```markdown
需要使用<SKILL: z-git> （如果没有z-git这个skill，可以无视分支管理这部分，或者遵循其他分支管理的skill规范）或其他skill中的git的能力，基于`<dev-branch>`创建分支，`<type>`为`<servers>`或者`<apps>`
读取 `<产品文档>`，`contract/`目录下的结构和<proposal>文档，在`<子项目>`中实现这个需求，并覆盖单元测试直到通过。

你有如下待办：
- 代码实现
- 提交实现(commit changes)
- 覆盖单元测试
- 运行单元测试和修复问题
- 提交单元测试和修复(commit changes)
- 更新AGENTS.md   - 按照本次的修改内容，更新AGENTS.md的内容。
- 提pull request，结束流程，等待review
```

更新`todo`，为每一项子项目新增`todo`，并在subagent执行完成后完成这个`todo`，举例说明，假设有如下项目结构

```
servers/api   - 使用@general 委托开发`proposals`关于 server/api的变动
apps/web      - 使用@general 委托开发`proposals`关于 apps/web的变动   
```

会产生两个`todo`，在agent完成后，分别提一个pull request，让用户进行review。等待用户review通过，执行后续步骤。

### 代码实现

代码风格参照已经有的「前端」、或者「后端」，或者项目中代码规范的 skill/AGENTS.md

### 单元测试

单元测试覆盖是必须的
