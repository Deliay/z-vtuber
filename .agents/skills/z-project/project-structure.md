# Project Structure

如果是多项目的monorepo，则按照 [[mono-repo-project-structure.md]] 中的内容创建项目结构。

如果是单个服务，则根目录直接存放项目本身，但有额外几个存储项目:

- docs/             - 存放文档
- docs/product      - 存放产品文档
- docs/engineering  - 存放开发规范

创建项目后，如果根目录有`README.md`文件，则在这个文件中新增或更新项目结构，如果根目录有`AGENTS.md`也可以向这个文件写入项目结构，**禁止写入除了根目录外的其他AGENTS.md**。

已经存在的项目结构可以在根目录的`README.md`或`AGENTS.md`中找到相关信息。
