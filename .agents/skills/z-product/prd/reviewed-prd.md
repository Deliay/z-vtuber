# 产品文档审核通过

如果用户输入了一个`pull request`序号，则使用 `pull request` 相关的工具 checkout 对应的分支。如果用户没有给出`pull request`序号，而是给出一个<文档名称>，则在`docs/product/draft`目录下检索这个<文档名称>，如果没有则提示没有找到未审核的产品文档，并中断agent执行。

如果找到了，则将对应<文档名称>从 `docs/product/draft` 移动到 `docs/product/reviewed`。
