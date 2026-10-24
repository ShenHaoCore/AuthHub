# 环境配置

> 从 [README](../README.md) 拆出。讲的是"配置从哪来、哪份文件放什么、启动期会拦什么"。

## 目录

- [四个来源，优先级由低到高](#四个来源优先级由低到高)
- [三份文件的分工](#三份文件的分工)
- [配置里的说明文字用 `//` 注释，不用 `_comment_*` 伪键](#配置里的说明文字用--注释不用-_comment_-伪键)
- [本机私有密钥（User Secrets）](#本机私有密钥user-secrets)
- [启动期环境护栏](#启动期环境护栏)
- [EF 设计时工具的环境](#ef-设计时工具的环境)

---

## 四个来源，优先级由低到高

| # | 来源 | 放什么 | 入库 |
|---|------|--------|------|
| 1 | `appsettings.json` | **所有环境共用**的默认值 | ✅ |
| 2 | `appsettings.{环境}.json` | 该环境特有的取值 | Development / Production 都入库 |
| 3 | **User Secrets**（仅 Development） | 本机私有密钥（OAuth ClientSecret 等） | ❌ 在用户目录，不进仓库 |
| 4 | 环境变量 / 命令行参数 | 部署平台注入的连接串、口令、证书 | — |

这是 ASP.NET Core 的默认层序（`WebApplication.CreateBuilder` 已装配）。User Secrets
只在 Development 生效，故生产/容器只能靠环境变量——不会出现「本机文件盖过部署注入」的坑。

**为什么生产配置也入库**：它让「生产该长什么样」有唯一可 review、可被 CI 校验的依据，
不必去翻部署文档或凭记忆。代价是**绝不能往里写任何口令** —— 写进去即使后来删掉，
它也还在 git 历史里，这类事故没法回滚。

## 三份文件的分工

| 文件 | 定位 | 大致内容 |
|------|------|---------|
| `appsettings.json` | 所有环境的公共默认 | 安全默认（`RequireHttps=true`）、日志模板与降噪、限流基线、角色权限出厂基线。**不表达任何单个环境的意图**。它的连接串是一个**刻意留的假地址**（解析不了的主机名 + `CHANGE_ME` 占位符）：真值必须由部署平台注入，留一个看起来能用的默认值反而危险。 |
| `appsettings.Development.json` | 本机开发 | 本机数据库、`localhost` 的 Issuer、开着文档与密码流程、跑种子与启动迁移。 |
| `appsettings.Production.json` | 生产 | 关文档、关密码流程、关种子与自动迁移、CORS 骨架、日志级别。**不含任何凭据**。 |

一个反例值得记住：`AuthHub:Features:EnableApiDocs` 的默认值**需要随环境变**（开发开、
其余关），所以它的默认值留在代码里（`Program.cs` 的 `GetValue` 第二个参数），**不**在
`appsettings.json` 里写 `false`。配置里的键一旦存在，代码里的默认值就永远不会生效 ——
那样就变成了「两份文件必须同时改才不会出错」，而这正是最容易被改崩的一种结构。

## 配置里的说明文字用 `//` 注释，不用 `_comment_*` 伪键

三份入库的 `appsettings*.json` 里都带大段中文说明（每个开关「为什么是这个值、什么场景下才改」）。
它们是**真注释**（JSONC 风格），不是配置项：

```jsonc
{
  // 部署在反向代理 / 网关之后时置 true。否则 RemoteIpAddress 会是网关地址（审计日志失真）……
  "TrustForwardedHeaders": false
}
```

.NET 的 JSON 配置提供程序（`JsonConfigurationProvider`）原生支持 `//` 与 `/* */` 注释，
读取时直接跳过。**不要改回 `"_comment_xxx": "说明"` 那种伪键** —— 伪键看着像注释，实际是
真实配置项，代价有三层：

1. 会进配置树（`AuthHub:Security:_comment_TrustForwardedHeaders` 是能读出来的键）；
2. 能被环境变量覆盖（`AuthHub__Security___comment_TrustForwardedHeaders`）；
3. 将来一旦对某一节改用强类型绑定并开 `ErrorOnUnknownConfiguration`，
   进程会**直接起不来**。

代价换来的唯一好处是「严格 JSON 工具也不会坏」，而这份仓库里并没有这类消费者：
CI 不解析配置、`docker build` 只是复制文件、`dotnet ef` 走的是同一个配置提供程序。

`launchSettings.json` 早就用着 `//` 注释（`dotnet run` 按 `JsonCommentHandling.Skip` 解析），
所以这不是引入新约定。守门用例见 `ShippedConfigurationFilesTests`：一个用例用**运行时同一个**
配置提供程序把三份入库文件各读一遍（注释写坏、漏逗号都会当场失败），另一个禁止 `_comment*` 键出现在
配置树里。

> 编辑器若给注释画波浪线（VS Code 默认会），把 `appsettings*.json` 关联到 `jsonc` 即可：
> `"files.associations": { "appsettings*.json": "jsonc" }`。纯显示问题，不影响构建与运行。
> 仓库根目录的 `.vscode/` 在 `.gitignore` 里，因此这条关联只能各自在本机设置。

## 本机私有密钥（User Secrets）

需要写入机密配置时用框架自带的 User Secrets，**不要**写进任何 `appsettings*.json`。
查看 / 清空：`dotnet user-secrets list`、`dotnet user-secrets clear`。机密落在
`%APPDATA%\Microsoft\UserSecrets\authhub-api-secrets\`。改完后需**重启**。

当前登录面为**账号密码**（及可选 MFA）；不含第三方 IdP。

## 启动期环境护栏

环境串用（开发配置被带进生产、生产没改占位符、受保护环境却开着接口文档）的后果几乎都是
**静默**的：连接串指向本机实例时表现为「启动很慢然后超时」，看不出是连错了地方。而这些都是
「看一眼配置就能确认」的事，所以放到启动期一次性问清楚。

| 检查 | 判据 | 严重度 |
|------|------|--------|
| 连接串是本机专用写法 | Server 是 `(local)` / `(localdb)` / `.` | **Fail** |
| 连接串还是占位符 | 含 `CHANGE_ME` | **Fail** |
| Issuer 为空或仍是模板 | 空字符串、或含 `example.com` | **Fail** |
| 接口文档被打开 | `Features:EnableApiDocs=true` | **Fail** |
| 连接串指向 loopback | `localhost` / `127.0.0.1` / `::1` | Warn |
| 传输层放宽 | `Security:RequireHttps=false` | Warn |
| 密码流程被打开 | `Features:EnablePasswordFlow=true` | Warn |
| 用 SQLite | `Database:Provider=Sqlite` | Warn |

（PostgreSQL 是部署级提供程序，与 SQL Server 同列，不触发上面任何一项；
PG 连接串的 `Host=localhost` 同样命中「连接串指向 loopback」的 Warn。）

Fail 与 Warn 的分界是**「在部署环境里是否必然不成立」**：`(local)` 走共享内存、`(localdb)`
是 LocalDB 实例，离开本机进程就不可达，没有任何凑合能跑的可能；而 `localhost` 在单机部署、
`RequireHttps=false` 在网关卸载 TLS 时都是**正当形态** —— 把它们判成 Fail 只会把正常部署
挡在门外，然后护栏被人直接关掉。

**只检查受保护环境**，判据是「不是 Development、也不是 Testing」而不是「是 Production」：
自定义的环境名（Staging、PreProd）因此天然也被保护。漏保护的代价是某个环境悄悄跑着开发
配置，多保护的代价只是多一次启动失败 —— 两者不对称，所以往严的一边倒。`Testing` 必须豁免：
集成测试夹具的配置（SQLite 临时库、明文演示口令、`RequireHttps=false`）就是「不该拿去部署」
的样子，检查它会全线误伤。

豁免开关 `AuthHub:Security:GuardMode` = `Strict`（默认）/ `Warn`（全部降级成日志）/ `Off`。
缺失或写错都按 `Strict` —— 出错方向必须是查得更严，否则一个拼写错误就成了安静的绕过开关，
而「写错了」恰恰是最常见的状态。

判定逻辑是纯函数（`Infrastructure/Configuration/EnvironmentGuard.cs`），
规则表由单元测试逐条钉住；「它确实被接进了启动序列」由集成测试钉住 ——
一个写了检查却没人调用的护栏，与完全没有护栏在行为上是无法区分的。

## EF 设计时工具的环境

`dotnet ef` 的连接串来源与运行时同构：`AUTHHUB_CONNECTION` 环境变量 →
`appsettings.{ASPNETCORE_ENVIRONMENT}.json` → `appsettings.json`，
读的是**启动项目**（`src/AuthHub.Api`）的那份文件。

它**刻意不提供任何本机默认值**。以前这里硬编码了一个本机连接串，后果是那个值会跟着代码走到
任何机器上：在 CI 或生产机器上执行 `dotnet ef` 时，命令会安静地连**本机**实例 —— 要么失败得
莫名其妙，要么更糟：那台机器上恰好有个同名库，于是结构被改在了一个完全无关的数据库上。

```bash
# 用 Development 的配置（默认）
dotnet ef migrations add Xxx --project src/AuthHub.Infrastructure --startup-project src/AuthHub.Api

# 临时指向别处
AUTHHUB_CONNECTION="Server=...;Database=...;" dotnet ef database update --project src/AuthHub.Infrastructure
```