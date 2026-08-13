# Project Structure of Monolithic Repository

如果是多服务的项目，推荐结构如下（只是距离）

servers/           - 存放后端项目
apps/             - 存放前端项目
packages/         - 存放公共项目
docs/             - 存放相关文档
docs/product      - 存放产品文档
docs/product/draft    - 存放尚未经过review的产品文档
docs/product/reviewed - 实现中或尚未实现的的产品文档
docs/product/shipping - 开发中的的产品文档
docs/product/released - 已合并且通过验收的产品文档
docs/engineering  - 存放开发规范
docs/engineering/proposals/ - 存放开发技术文档
contracts/        - 存放开发契约文件（如openapi-spec, graphql-schema）
tests/            - 存放集成测试、API测试、e2e测试项目

例如（只是举例）：
```
servers/domainA-api
servers/domainB-api
servers/product-bff
servers/product-gateway

apps/domainA-web
apps/domainB-web
apps/domainA-andriod
apps/domainA-flutter
apps/domainB-react-native

packages/common-package-A
packages/common-package-B

docs/product/draft/20260415-some-domain-featrue.md
docs/product/shipping/20260415-another-domain-featrue.md
docs/product/released/20260415-one-another-domain-featrue.md

contracts/domainA.graphql
contracts/domainB-openapi.yaml
contracts/domainC-websocket.md

tests/domainA-api-test
tests/domainA-web-e2e
```

## 项目命名

项目的命名的部分一定要与其业务或者功能有强关联，举例如下：

```bash
# BAD! 无法分辨这个项目是用来做什么的
server/zues

# Good! 直观的知道这个项目的用途
server/api

# Good! 既体现了个性化的项目名称，又能直观的知道这个项目的用途
server/zues-api
```
