# 前端开发规范

本文档描述 Z.Vtuber 项目前端（React）的统一规范。

## 技术栈

- 语言：TypeScript
- 框架：React 19
- 构建工具：Vite
- 测试框架：Vitest + React Testing Library
- 包管理器：npm

## 初始化指令

```bash
npm create vite@latest . -- --template react-ts
```

## 项目结构

```
apps/web/
├── src/
│   ├── App.tsx
│   ├── main.tsx
│   └── index.css
├── index.html
├── vite.config.ts
├── package.json
└── tsconfig.json
```

## 组件命名

- 组件文件使用 PascalCase。
- 组件目录使用小写 kebab-case。

## 状态管理

- 项目初期使用 React 内置状态管理。
- 复杂状态时引入 Zustand。

## 契约（codegen）

- 前端基于 `contracts/` 目录的 OpenAPI 规范生成 API 客户端。
- 通过 `VITE_API_URL` 环境变量配置后端地址。

## 相关规范引用

- `docs/engineering/common-rules.md`
- `docs/engineering/frontend-rules.md`
