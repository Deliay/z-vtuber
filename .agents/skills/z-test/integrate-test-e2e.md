# 集成 e2e test

搜索技术方案`<proposal>`及 contract/ 目录下的文件，找出所有服务中可能的前端项目或者app项目，技术栈是前端的，具有UI代码的，都应该覆盖e2e测试。

## 分支管理

使用<SKILL: z-git> （如果没有z-git这个skill，可以无视分支管理这部分，或者遵循其他分支管理的skill规范）或其他skill中的git进行分支管理，每次进行集成API test都会基于`<dev-branc>`创建新的分支，`<type>`为`e2e`。在这个新的分支进行项目开发

## 项目路径

统一放置在`tests/e2e/`文件夹下，对于基于浏览器的web应用使用playwright框架来进行测试，其他应用则使用对应技术栈的e2e测试框架进行测试

如果还没有创建相关项目，则显示一个选型的`ask`或`question`工具，让用户进行选择。

## 目录结构

假设我们有两个前端web项目`apps/web-a`, `apps/web-b`，则API集成测试结构如下
（这个结构仅为使用nodejs相关框架的示意，具体要根据技术栈来调整。）

```
tests/e2e/src/web-a/    # 存放web-a的测试
tests/e2e/src/web-b/    # 存放web-b的测试
tests/e2e/src/tools/    # 存放用于调查问题的临时脚本
tests/e2e/package.json  
```

## 编写 e2e test

使用`@general`subagent 进行编写，要求加载`contract/`目录下的相关contract与`proposal`内容和`<产品文档>`，覆盖所有可能的页面场景和产品验收点。禁止对照`servers/`里的实现来写case，避免出现射箭画靶的情况。

e2e test之后提一个`pull request`让用户进行review，`pull request`的`description`写出你的case列表，review通过之后开始运行 e2e test

## 运行 e2e test

使用`@general`subagent 执行测试：用 `aspire`(SKILL: aspire and [[infra-orchestrator.md]]) 拉起所有服务，并根据`tests/e2e`的技术栈运行测试。

启动的目标后端endpoint可以通过`aspire describe <service-slug>`获取，如`aspire describe web-a`即可拿到endpoint，这里推荐通过环境变量将endpoint注入给测试项目。

## 测试编写标准

每个e2e测试在编写时**必须**有截图进行验证，开始操作的截图，操作完成的截图，需要有明确的操作路径验证。如果无法在UI上有所体现，则进行network的inspect。在测试编写完成时需要截图验证，但在测试编写完成后，去掉截图验证的逻辑。

## 修复测试发现的问题

如果 test case 执行后发生不符合预期，或者出现报错的情况，使用 `@general` subagent 委托进行修复：让其加载`docs/engineering`与对应项目的`AGENTS.md`内容，然后传递出错的API和`aspire`上下文给 subagent，后进行修复，直接在这次test的分支上修复即可。

e2e test出错，则可以同时调度多个subagent进行修复。等待subagent全部修复完成后，会再次针对这个API进行验证。

## 运行成功

如果所有的case 都运行成功，则在`pull request`中更新本次测试新增或修改的case和发现的问题和修复情况，并将`pull request`合并到`<dev-branc>`
