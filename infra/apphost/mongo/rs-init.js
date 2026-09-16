// 单节点 MongoDB replica set 初始化：mongo 容器以 --replSet rs0 启动，
// 首次初始化数据目录时由 docker-entrypoint 执行本脚本，将单节点提升为 primary。
// 脚本幂等：已初始化（rs.status 成功）时跳过；未初始化（code 94）时执行 rs.initiate。
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
