# 修改产品文档

当用户想修改draft的产品文档时，你需要要求用户提供pull request的序号，你使用`pull request`相关的工具读取对应的分支。

如果`pull request`中有review的comment，或者是inline的comment，你需要分析这些comment是否已经被修复过了，如果还没有修复则进行修复。你也可以就修改的问题与用户交流。

上述操作完成后中断agent循环，如果没有review comment，则提示用户你已准备好修改产品文档，询问用户哪些地方需要修改。如果有review comment，完成后再次询问用户是否还有修改。

如果没有则`commit and push your changes`。
