# 测试与验证

> 从 [README](../README.md) 拆出。

```bash
# 全解决方案（编译须 0 警告 0 错误）
dotnet build AuthHub.slnx

# 单元测试：159 项
dotnet test tests/AuthHub.UnitTests

# 集成测试：133 项
dotnet test tests/AuthHub.IntegrationTests
```

## 目录

- [覆盖内容](#覆盖内容)
- [端到端脚本](#端到端脚本)
- [Postman 集合](#postman-集合)

---

## 覆盖内容

| 项目 | 用例数 | 覆盖内容 |
|------|--------|----------|
| `AuthHub.UnitTests` | 159 | 角色-权限映射**三级叠加语义**（出厂默认 / 配置基线 / 数据库覆盖各自生效、**覆盖行内空数组必须收回全部而不是回落配置**、`IsCustomized` 判定、`Invalidate()` 后确实重读、快照确实被缓存）、**反代信任范围解析**（IPv4/IPv6 CIDR、裸 IP 与前缀超界被拒且消息带原值、空白项跳过、`Describe` 必须渲染成 CIDR 而不是类型名）、**种子护栏**（四种组合，含"生产 + 缺二次确认必须拒绝"）、**环境护栏**（受保护环境的判定表含自定义环境名、`(local)`/`(localdb)`/`.` 判 Fail 而 `localhost`/`::1`/`tcp:localhost,1433` 只判 Warn、占位符哨兵、受保护环境用 SQLite、`GuardMode` 降级与**非法值必须回退到 Strict**）、**本机覆盖文件的插入位置**（"晚于 `{环境}.json`"与"早于环境变量"两个方向各有用例，另有一条直接断言配置源顺序 —— 只看最终取值可能是巧合）、`Result` / `PagedResult` 语义、FluentValidation 规则（含重定向 URI 白名单）、**`RolePermissionOptionsValidator`**（默认值通过、未声明权限/大小写不符/空白项各自被拒且报出 offender、空列表合法、自定义角色合法）、`AccountService` 的登录/注册分支（Moq 构造 `UserManager`/`SignInManager`，含"未知用户不得泄露账号存在性"）、**MFA 下发通道**（目标是通道自己解析的、验证码真的进了文案、两条通道互不串台、缺联系方式与未知通道都判校验失败） |
| `AuthHub.IntegrationTests` | 133 | 发现文档、**用公钥集对令牌做真实 RSA 离线验签**、JWS 令牌内容（sub/scope/role/iss/aud）、错误密钥与未知客户端、登录页与防伪令牌、安全响应头、未见令牌 401、会话 Cookie 不能认证 Bearer API、scope 门禁 403、M2M 令牌被 userinfo 拒绝、令牌端点与 HTML 登录表单的限流 429、**错误响应契约**（五条 400 出口的字段集与媒体类型必须一致）、**OpenIddict 声明目标映射**、**API 文档端点**（Scalar 页面及其内嵌脚本资源、OpenAPI JSON 里的 OAuth2 方案、`/connect/*` 未被可见性约定丢掉、**页面型端点不混进文档**、旧 Swagger 路径已下线、未开启时不暴露）、**角色权限配置**（仓库 `appsettings.json` 的默认值必须与代码出厂默认逐字一致、未声明权限让启动失败、配置的权限能一路走到 `IRolePermissionMap` 而无需改代码）、**后台改权限归属**（渲染出可提交的复选框、仅被覆盖过才露出「恢复默认」、保存后权限立即生效且落审计、空选择=收回全部、恢复默认回落基线、未声明权限名被拒、摘掉最后一个 `roles.manage` 被拒且状态不变）、**部署安全**（密钥环真的落到配置目录、**两个实例共享目录能互相解密**、非法网段让进程起不来且消息带原值、TrustAnyProxy 不影响正常服务）、**环境护栏**（受保护环境里连接串指向本机专用地址时阻止启动、消息带出配置项路径与豁免开关；`GuardMode=Warn` 降级后服务照常提供；**`Testing` 环境必须豁免同一套规则** —— 否则整套集成测试会在加上护栏的当天集体变红）、**管理后台**（7 个页面未登录一律 302 到登录页且不发 Bearer 挑战、无权限用户 302 到 `/account/denied`、管理员逐页可访问且渲染出关键内容、页面零站外引用、5 个静态资源可取且 Content-Type 正确、**静态资源可缓存失效**（响应带 `no-cache`、页面引用带内容指纹）、协议页与后台共用同一份样式令牌、**flex/grid 容器内相邻卡片不继承流式外边距**、Scope 页表单完整往返一次创建与删除、审计页时间区间筛选在两种提供程序下都可用、**弹窗的无障碍名称与关闭按钮类型**、**所有图标共用同一份 SVG 契约**、**页内锚点须由 hashchange 监听接管**）、**随包配置文件**（四个 `appsettings*.json` 都能被运行时同一个配置提供程序读出来 —— 注释写坏、漏逗号这类只在起进程时才暴露的错误因此进不了主干；且说明文字不得写成 `_comment_*` 伪键） |

集成测试用 `WebApplicationFactory<Program>` 起真实管道（认证中间件顺序、限流、CORS、
安全头都参与），数据库用独立的临时 SQLite 文件，跑完即删。

> 后台的会话用例走的是 `TestServer.CreateHandler()` + 自建 Cookie 容器
> （`Infrastructure/CookieSession.cs`），**刻意不复用 fixture 上那个共享 `Client`** ——
> 否则登录后残留的会话 Cookie 会让"未登录应返回 401"那几条断言随机失败。

> **注意**：集成测试会占用 `src/AuthHub.Api/bin` 下的 DLL。如果本机正跑着
> `dotnet run`（或在 Visual Studio 里调试），先停掉它，否则会拿到
> `MSB3021 / MSB3027: 文件被 AuthHub.Api (PID) 锁定`。
> 不想停调试时，也可以换个配置跑：`dotnet test -c Release`
> （IDE 的调试通常占用 Debug 目录，两边互不干扰）。

## 端到端脚本

`.tmp/flow_test.py`（纯标准库，无第三方依赖）对着已启动的服务跑一遍完整链路，
共 **33 项断言**，覆盖所有功能：

```bash
dotnet run --project src/AuthHub.Api          # 另开一个终端
python .tmp/flow_test.py
```

内容包括：错误密码被拒 → 正确密码登录 → 同意页 → 授权码 + PKCE 换令牌 → 令牌内容校验 →
userinfo → 受保护 API → scope 门禁 → 管理员权限声明 → 撤销后过期前 JWT 立即失效 →
刷新令牌旋转 → 旧 refresh_token 复用被拒且整条授权被撤销 → `prompt=none` 静默签发 →
登出后 `login_required` → 客户端凭证。脚本已经加了 `.gitignore`，它是开发期工具，不随仓库发布。

## Postman 集合

`docs/AuthHub.postman_collection.json` 是可直接导入的集合，按五个文件夹组织，
互相之间有令牌传递（`accessToken` / `refreshToken` / `adminToken` 都是集合变量）。

| 文件夹 | 内容 |
|--------|------|
| 0. 环境与发现 | 健康检查、发现文档、JWKS 公钥集 |
| 1. 机器对机器 | 客户端凭证换令牌、访问受保护资源、scope 门禁、**无令牌 401 的 RFC 7807 形状** |
| 2. 用户会话 | JSON 登录（Cookie）、查询当前用户、登出 |
| 3. 授权码 + PKCE | 放行 Postman 回调地址、OAuth2 助手拿管理员令牌、管理员接口 |
| 4. 刷新令牌与撤销 | 令牌旋转、按 subject 批量撤销、撤销后立即 401 |

导入后需要先做两步：在 Postman 设置里**关闭 SSL 证书校验**（本机是自签名开发证书），
以及在本机以 Development 环境启动服务（否则没有种子客户端和账号）。