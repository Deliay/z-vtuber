---
name: z-aspire-orchestrator
description: 'USE FOR: service orchestration, aspire, docker, podman'
---

# 编排服务

我们在`infra/`文件夹下进行项目的编排，使用`aspire`及其`skill`。

## 优先使用SKILL

在你不确定的时候，优先从已经有的SKILL中检索知识。有SKILL则使用SKILL。

## 初始化aspire skill

如果用户环境没有安装aspire，则中止agent流程，如果用户显式说明不用aspire进行编排，则跳过编排流程。

如果用户环境安装了`aspire`，则引用 [[aspire.md]] 的知识进行后续操作。

检查脚本
```bash
aspire --version
```

## 初始化编排环境

在执行到编排环节时，编排服务需要使用<SKILL: z-git> （如果没有z-git这个skill，可以无视分支管理这部分，或者遵循其他分支管理的skill规范）或其他skill中的git的能力，基于`<dev-branch>`创建分支，`<type>`为`infra`。如果没有`infra/local-dev`目录，则默认为用户创建用于本地开发编排的`aspire`项目，在`infra/local-dev`目录下

如果还没有`local-dev`的aspire项目，则新建项目，执行 `aspire init --language typescript` 进行新建项目。

如何维护使用TypeScript语言的Aspire项目可以使用`aspire docs get typescript-apphost-project-structure`命令获取帮助。

如果已经有了`infra/local-dev`目录，则无需初始化环境。

## 分析项目可能的依赖

搜索技术方案`<proposal>`, 开发分支中的项目引用的包，找到可能的环境变量，并分析出项目之间的依赖关系。如果依赖关系有变化则进行修改，没有则跳过本步骤。

## 完成编排

在编排完成后，在`local-dev`文件夹使用`npm run aspire:build`和`npm run aspire:lint`来编译编排脚本，并检查改动是否正确。

```bash
npm run aspire:lint
npm run aspire:build
```

并检查各个服务都应该启动成功：

```bash
aspire stop
aspire restore
aspire start
aspire describe
```

然后提`pull request`要求用户review，直到用户显式说明review通过，或者pull request状态已经是merge了，再进行下一个步骤。

## 依赖的中间件设施

使用`SKILL: aspire`来编排中间件设施，可以通过cli搜索对应的文档`aspire docs search <keyworkd>`，这里的keywor模板可以是 `get-started-with-the-<目标技术栈>-integration`，例如：

```bash
# Redis
aspire docs search get-started-with-the-redis-integration

# postgresql
aspire docs search get-started-with-the-postgresql-integration
```

## TypeScript / NodeJS 相关文档的搜索

统一使用`javascript`的文档来进行替代: `aspire docs search javascript`

## 编排技巧

### 指定服务镜像Tag

使用 `.withImageTag(tag: string)`可以覆盖Docker Resource的tag

### JavaScript App集成

使用 `addJavaScriptApp` API，第三个参数是 `{ runScriptName: "" }` 这样的对象，如果你想执行`dev` 这个脚本例如：

```typescript
  .addJavaScriptApp('service-name', 'project_relative_path', { runScriptName: 'dev' });
```

### 服务依赖设置

分析后端服务分别对中间设置的依赖，使用如下pattern进行依赖，`withReference`是将对应资源的`parameter resource`注入到目标服务中，`waitFor`则是标记这个服务需要等到目标服务成功启动，health check完成后再进行启动。

```typescript
const redis = await builder.addRedis();

const backend = await builder
  ...some setup...
  .withReference(redis).waitFor(redis);
```

### 环境变量注入

我们可以通过`.withEnvironment()`注入环境变量，可以从其他的resource中获取对应的endpoint，注意 api.getEndpoint这个API需要await，示例如下：

```typescript
const mongo = await builder.addMongoDB();

const backend = await builder.addJavaScriptApp()
  .withEnvironment("MY_CUSTOM_BACKEND", await mongodb.uriExpression.get());

const frontend = await build.addJavaScriptApp()
  .withEnvironment("API_URI", await api.getEndpoint('http'))
```

### 开放外部端口

使用`.withHttpEndpoint`开放一个外部端口，使用这个方法会增加名为`http`的endpoint，用于外部访问，比如我们的前端服务，后端服务都需要一个外部端口来进行访问。下面是一个示例：

```typescript
const api = await builder.addJavaScriptApp()
  .withHttpEndpoint({ env: 'PORT' }); // 使用 env将端口号注入到环境变量

const frontend = await build.addJavaScriptApp()
  .withReference(backend)
  .withEnvironment("API_URI", await api.getEndpoint('http'))
```

### lint提示找不到tsconfig.json

创建一个tsconfig.json，引用aspire的apphost tsconfig即可：

```json
{
  "extends": ["tsconfig.apphost.json"]
}
```
