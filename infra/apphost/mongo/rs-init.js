// 单节点 MongoDB replica set 初始化：mongo 容器以 --replSet rs0 启动，
// 首次初始化数据目录时由 docker-entrypoint 执行本脚本，将单节点提升为 primary。
// 脚本幂等：已初始化（rs.status 成功）时跳过；未初始化（code 94）时执行 rs.initiate。
//
// 重要：docker-entrypoint 仅在数据目录为空的首次启动时执行 /docker-entrypoint-initdb.d 下的脚本。
// 复用已有的 standalone 数据卷时本脚本不会执行，副本集将无 primary（事务永久失败）。
// 复用旧卷前请删除/重建数据卷，或手动执行 rs.initiate。详见 infra/apphost/AppHost.cs 顶部说明。
try {
  rs.status();
  print("MongoDB replica set rs0 already initialized");
} catch (err) {
  print("Initializing MongoDB replica set rs0: " + err.message);
  rs.initiate({
    _id: "rs0",
    members: [{ _id: 0, host: "mongo:27017" }]
  });
}
