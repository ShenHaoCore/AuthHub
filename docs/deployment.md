# 部署

> 从 [README](../README.md) 拆出。

## 目录

- [配置（生产）](#配置生产)
- [容器 / 反代部署的两个隐性依赖](#容器--反代部署的两个隐性依赖)
- [迁移](#迁移)
- [反向代理](#反向代理)
- [Docker](#docker)

---

## 配置（生产）

**非敏感项已经在入库的 `appsettings.Production.json` 里**（`RequireHttps`、文档开关、
密码流程、种子与自动迁移开关、日志级别），不必在这里重复 —— 重复一次就多一处要同步的地方。
所以下面这份清单只列**必须由部署平台注入**的东西：

```bash
ASPNETCORE_ENVIRONMENT=Production

# 必配：连接串。护栏会拒绝 (local) / (localdb) 这类本机专用写法，
# 也会拒绝还带着 CHANGE_ME 占位符的值。
ConnectionStrings__DefaultConnection="Server=...;Database=AuthHub;User Id=...;Password=...;Encrypt=True;"

# 必配：必须是这个部署实际对外可达的地址，且结尾带 /。
# 模板里的 authhub.example.com 是占位符 —— 留着它进程会直接起不来（护栏的 Fail 项）。
AuthHub__Issuer="https://auth.example.com/"

AuthHub__Keys__SigningCertificatePath="/run/secrets/authhub-signing.pfx"
AuthHub__Keys__SigningCertificatePassword="..."

# 仅前后端分离时需要；不配置时会收紧成 https://localhost（而不是放开）
AuthHub__Cors__AllowedOrigins__0="https://app.example.com"

# 必配（容器部署）：Data Protection 密钥环要落到挂载的卷上。
# 不配的话密钥环在镜像层里，容器一重建就换一套：所有人被登出、
# 后台表单提交 400、MFA 流程中断，而日志里只有一行"默认位置"。
AuthHub__Security__DataProtectionKeysPath="/var/authhub/keys"

# 反代部署必配：告诉它信任谁。.NET 默认**只信任 loopback**，
# 反代在另一个容器 / 另一台机器时，X-Forwarded-* 会被静默丢弃。
AuthHub__Security__TrustForwardedHeaders=true
AuthHub__Security__KnownNetworks__0="172.16.0.0/12"   # CIDR，必须写前缀长度
AuthHub__Security__KnownProxies__0="10.0.0.5"         # 或直接列网关地址（可同时用）
# AuthHub__Security__TrustAnyProxy=true               # 上游地址不固定时的出口，见下

# 可选：部署期基线，覆盖角色→权限映射（不写则用代码出厂默认，见「权限模型」）。
# 只列要改的角色即可；权限名写错会让进程起不来（启动期校验）。
# 注意：这只是**基线**，后台在 /admin/roles 上的勾选（落 RolePermissionOverrides 表）
#       优先级更高，会盖过这里配的值。想让它说了算，就别在后台改该角色。
AuthHub__RolePermissions__Auditor__0="audit.read"
AuthHub__RolePermissions__UserManager__0="users.manage"
AuthHub__RolePermissions__UserManager__1="tokens.revoke"
AuthHub__RolePermissions__UserManager__2="audit.read"   # 例：把审计只读也给了用户管理员

# 可选：资源指示器白名单（RFC 8707）。接入 MCP Server 时填它的地址，
# 令牌的 aud 才能绑定到它。必须写绝对 URI，否则启动直接失败。
AuthHub__Resources__0="https://mcp.example.com/mcp"
```

**签名密钥必须持久化**：`Extensions/OpenIddictKeySetup.cs` 会按 配置证书 → 开发证书 → 临时密钥 依次降级。
临时密钥每次重启都变，会让所有已签发令牌失效、下游缓存全废 —— 生产必须走持久化证书。

**这份配置在启动期会被自动检查一遍。** 受保护环境（不是 Development、也不是 Testing，
因此 Staging / PreProd 同样算）下，以下情况**直接拒绝启动**：连接串是本机专用写法
（`(local)` / `(localdb)` / `.`）、连接串还带 `CHANGE_ME` 占位符、`Issuer` 为空或仍是
`example.com`、接口文档被打开。另有几项只写警告：连接串指向 loopback、`RequireHttps=false`、
密码流程打开、用 SQLite。

如果某个警告项的取值确实是有意为之（典型两种：TLS 在网关卸载所以 `RequireHttps=false`、
数据库就在同一台机器上所以用 `localhost`），**不必关掉整个护栏**，降一级即可：

```bash
AuthHub__Security__GuardMode=Warn   # 全部降级成启动日志；Off 则完全跳过
```

完整的规则表、以及「为什么这几条判 Fail 而那几条只判 Warn」，见
[环境配置 · 启动期环境护栏](configuration.md#启动期环境护栏)。

## 容器 / 反代部署的两个隐性依赖

这两样东西**配错了不会让进程起不来**，只会在运行期以别的面目出现，因此都在启动日志里显式打出来。

### ① Data Protection 密钥环（`AuthHub:Security:DataProtectionKeysPath`）

它保护的不是"记住我"那类小玩意，而是三样东西：**会话 Cookie 的加密票、防伪令牌、MFA 票据**。
默认位置是 `$HOME/.aspnet/DataProtection-Keys`，而容器里这个位置通常不在卷上 ——
容器一重建密钥环就换一套，表现为「所有在线用户被登出 + 后台表单提交 400 + MFA 流程中断」。

- **必须挂卷**，并且**多实例 / 滚动更新必须共享同一个目录**，否则 A 实例发的 Cookie 到 B 实例解不开。
- 目录不可写（只读卷、错误的挂载）会**在启动期直接抛**，不会拖到第一次签发 Cookie。
- 密钥环的应用名被固定成常量 `AuthHub`（`DataProtectionExtensions.ApplicationName`）：
  默认隔离键由**内容根路径**派生，两个实例从不同路径启动就认不到同一个环，共享目录也救不回来。
  **这个名字不该再改** —— 改了等于把所有已发 Cookie 作废一次。

### ② 反向代理信任范围（`AuthHub:Security:TrustForwardedHeaders` + `KnownProxies` / `KnownNetworks`）

`ForwardedHeadersOptions` 的信任范围**默认只含 loopback**（`127.0.0.1/8` 与 `::1`）。
把 Nginx 放在另一个容器或另一台机器上时，它发来的 `X-Forwarded-For` / `X-Forwarded-Proto`
会被 ASP.NET Core **静默丢弃** —— 不报错、不打日志。症状是审计日志里的来源 IP 全部记成网关地址，
以及 `RequireHttps` 下 `Request.IsHttps` 恒为 false 带来的重定向异常。

| 配置形态 | 效果 |
|----------|------|
| `KnownNetworks` / `KnownProxies` | 显式列出网段 / 网关地址（推荐）。**配了任意一项就整体替换默认值**，而不是叠加 —— 追加语义会让"我明明只信任 172.16/12"与"其实还信任着 loopback"并存，读配置时看不出全貌 |
| `TrustAnyProxy=true` | 上游地址不固定时（容器编排常见）的显式出口，等同信任任何直连来源。会打一条警告：安全性此时完全由网络层保证 |
| 都不配 | 沿用 loopback 默认值，并**打一条警告**说明这意味着什么 |

- 网段必须写成 CIDR 且**写出前缀长度**，裸 IP 会被拒绝并让进程起不来 ——
  替用户把 `10.0.0.5` 猜成 `/32` 看着贴心，但同一个人手滑写成 `10.0.0.0` 时该猜 `/32` 还是 `/8` 就没有依据了，
  而两者授权范围相差 1600 万个地址。
- 解析失败**不降级**成"空信任范围继续跑"：那会退化成"转发头全被忽略"这个最难排查的症状。

## Redis 令牌条目缓存（可选）

`AuthHub:Redis:Enabled=true` 时，`EnableTokenEntryValidation()` 的"每个带令牌的 API 请求查一次库"
变为查 Redis（未命中才回库并回填）。**默认关闭**：单实例部署不需要它，开启前先把 Redis 备好。

```bash
AuthHub__Redis__Enabled=true
AuthHub__Redis__ConnectionString="localhost:6379,abortConnect=false"
AuthHub__Redis__EntryTtlMinutes=60          # 兜底 TTL，见下
AuthHub__Redis__KeyPrefix="authhub:tokens:" # 多套环境共用一个 Redis 时用来隔离键空间
```

**撤销仍然立刻生效**，这是设计红线：

| 变更类型 | 失效方式 |
|---|---|
| 令牌级撤销 / 兑换（管理后台、api/tokens、刷新令牌轮换） | 精确删除该令牌的缓存键，下一次校验立刻回库 |
| 批量撤销（`RevokeBySubjectAsync` 等全部 `Revoke*`）与清理（`PruneAsync`） | 递增全局版本号 —— 所有旧条目立刻判为过期，由 TTL 回收 |

条目快照里带并发令牌，刷新令牌重用检测依赖的乐观并发（`ConcurrencyException`）语义不变。

**故障降级**：Redis 连不上 / 超时 / 协议错 → 只记一条节流警告（30 秒一条），直查数据库。
认证不受影响；唯一代价是**故障窗口内发生的撤销**要等 TTL 过期才对缓存可见 ——
这就是 `EntryTtlMinutes` 的真正含义（故障恢复一致性的上界），不是缓存命中率旋钮。
连接串刻意带 `abortConnect=false`：Redis 暂时缺席时后台重连，而不是让首次缓存操作抛死。

**多实例部署注意**：

- 所有实例必须指向**同一个 Redis、同一个 KeyPrefix**，否则等于每套实例各一份缓存，
  "批量撤销立刻生效"只在递增了版本号的那份里成立。
- 缓存不改变"单实例也能跑"的前提：关掉开关，行为与引入之前完全一致。
- 键形如 `authhub:tokens:id:{令牌Id}` / `authhub:tokens:ref:{混淆后的引用Id}`，
  值内含写入时的版本号 —— 运维用 `SCAN MATCH "authhub:tokens:*"` 能一眼认出本服务的键。

## 迁移

```bash
dotnet ef migrations add <Name> -p src/AuthHub.Infrastructure -s src/AuthHub.Api -o Data/Migrations --context AuthHubDbContext
dotnet ef database update -p src/AuthHub.Infrastructure -s src/AuthHub.Api
```

## 反向代理

部署在 Nginx / 网关之后时设 `AuthHub__Security__TrustForwardedHeaders=true`，
否则 `RemoteIpAddress` 与 Scheme 会错，导致审计日志里记的是代理 IP、以及 HTTPS 判断异常。

## Docker

见 [`Dockerfile`](../Dockerfile)：

```bash
docker build -t authhub:local .
docker run --rm -p 8080:8080 \
  -v authhub-keys:/var/authhub/keys \
  -e ConnectionStrings__DefaultConnection="..." \
  -e AuthHub__Issuer="http://localhost:8080/" \
  -e AuthHub__Security__RequireHttps=false \
  -e AuthHub__Security__DataProtectionKeysPath="/var/authhub/keys" \
  authhub:local
```

> **`-v authhub-keys:...` 不是可选项**：容器内以非 root 的 `app` 用户运行，Data Protection
> 密钥环默认落在镜像层里。不挂卷的话，`docker run` 换一次容器就等于换了一套密钥 ——
> 所有会话 Cookie、防伪令牌与 MFA 票据一起失效。多副本部署时把同一个卷（或等价的共享存储）
> 挂给所有副本，见上文「容器 / 反代部署的两个隐性依赖」。