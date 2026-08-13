# 后端开发规范

本文档描述 Z.Vtuber 项目后端（C# / .NET）的统一规范。

## 技术栈

- 语言：C# (.NET 10)
- 框架：ASP.NET Core Web API
- API 文档：Microsoft.AspNetCore.OpenApi
- 测试框架：xUnit

## 初始化指令

```bash
dotnet new webapi -n Z.Vtuber -o servers/api --no-https --use-controllers
```

## 项目结构

```
servers/api/
├── Controllers/     # API 控制器
├── Program.cs       # 应用入口
├── Z.Vtuber.csproj  # 项目文件
```

## 契约（codegen）

- 契约文件存放于 `contracts/` 目录（OpenAPI YAML）。
- 后端为契约的生成源，前端基于契约生成客户端。

## 测试框架

- 使用 xUnit 编写单元测试和集成测试。
- 测试项目位于 `tests/` 目录。

## 相关规范引用

- `docs/engineering/common-rules.md`
- `docs/engineering/backend-rules.md`
