# 让 AuthHub 作为 MCP 的授权服务器

> 面向：要把一个 MCP Server 接进 AuthHub 的人；以及要实现 MCP Server 侧校验的人。
>
> 对应工单：[#5 资源指示器与 S256 收紧](https://github.com/ShenHaoCore/AuthHub/issues/5)、
> [#6 动态客户端注册](https://github.com/ShenHaoCore/AuthHub/issues/6)（未做）。

## 0. 先分清角色：AuthHub 只做授权服务器

MCP 的授权是三方结构，缺一方都跑不通：

| 角色 | 是谁 | 职责 |
|---|---|---|
| MCP Client | Claude Desktop / Cursor / 自研 Agent | 走 OAuth 2.1 拿令牌，带 `resource` 参数声明"我要访问谁" |
| **授权服务器（AS）** | **AuthHub（本文）** | 认证用户、签发 `aud` 绑定到具体 MCP Server 的令牌 |
| 资源服务器（RS） | MCP Server（另一个进程 / 项目） | 校验令牌的 `aud` 与 scope，然后执行 tool |

**"AuthHub 支持 MCP"有三种含义，别混为一谈：**

| | 是什么 | 与本项目的关系 |
|---|---|---|
| **A** | AuthHub 当 MCP 的授权服务器 | 本文做的事。已实现 |
| **B** | AuthHub 自己变成 MCP Server（暴露 tool 让 AI 管理用户/权限） | 另一条产品线。**可以和 A 共存**：同一个服务既当 AS 又当 RS 是常见形态（AuthHub 的 `/api/*` 本来就是它自己签的令牌的 RS），前提是 MCP 端点要独立校验 `aud`，不能因为是"自家签的"就放行 |
| **C** | 把 MCP 客户端当普通 OAuth client 手工登记 | 不是第三种方案，而是 A 的**子集**：不做 `aud` 绑定、只登记 client_id。只适合"自己内部 1-2 个客户端"的临时做法 |

## 1. 登记资源（必须）

令牌的 `aud` 白名单是**库表**（RegisteredResources），通过资源管理 API 登记，**变更即时生效，不需要重启**：

```bash
curl -X POST https://authhub.example.com/api/resources \
  -H "Authorization: Bearer <需要 resources.manage 权限的用户令牌>" \
  -H "Content-Type: application/json" \
  -d '{
    "resource": "https://mcp.example.com/mcp",
    "displayName": "MCP Server"
  }'
```

- 只有登记过且**启用中**的**绝对 URI** 才能出现在授权请求 / 令牌请求的 `resource` 参数里；未登记 → `invalid_target`。
- 资源标识入库前会规范化为 `Uri.AbsoluteUri` 形态（主机小写、默认端口收敛），请求原文与登记值做序数比对 —— 管理员不需要关心大小写与端口写法。
- 内置的 `authhub-api` **不要**登记进来：它作为 `aud` 是 Scope 的 `Resources` 带出来的（`AuthHubSeeder`），走的是另一条代码路径。
- `AuthHub:Resources` 配置**只做加法的幂等种子**：启动时把配置里的资源播种入库（已存在的跳过），并保留启动期 fail-fast（配置里的非法 URI 让进程起不来）。从配置里删掉的资源**不会**从库里移除 —— 下线资源请用管理 API 删除，或把登记置为停用（`Enabled=false`，等同未登记但保留记录）。
- 删除登记时，若仍有客户端持有该资源的 `rsrc:` 权限，API 返回 **409**：先收回客户端授权，再删除。
- 令牌签发后不回溯：删除/停用只影响后续签发，已发出的令牌到期前仍然有效（RS 侧的 `aud` 校验不受影响）。

## 2. 给客户端授权（资源权限）

登记只说明"这个资源存在"。某个客户端能不能拿它的令牌，看客户端权限里的 `rsrc:` 项：

```bash
curl -X POST https://authhub.example.com/api/clients \
  -H "Authorization: Bearer <需要 clients.manage 权限的用户令牌>" \
  -H "Content-Type: application/json" \
  -d '{
    "clientId": "mcp-demo",
    "displayName": "MCP 示例客户端",
    "grantTypes": ["authorization_code", "refresh_token"],
    "scopes": ["openid", "profile", "api:read"],
    "resources": ["https://mcp.example.com/mcp"],
    "redirectUris": ["http://localhost:33418/callback"],
    "requireConsent": true
  }'
```

`resources` 会被翻译成 `rsrc:<uri>` 权限项，`GET /api/clients/{clientId}` 的 `allowedResources` 可以核对。
更新时 `resources` 传 `null` 表示不动（与 `scopes` / `grantTypes` 同一套语义）。

> 管理后台的客户端编辑页可直接勾选「允许的资源」（v1.3.1 起，PR #14）；API 方式（`resources` 字段）继续可用。

## 3. 客户端怎么发请求

两处都要带 `resource`（MCP 授权规范要求），PKCE 只能用 **S256**：

```
GET /connect/authorize
  ?response_type=code
  &client_id=mcp-demo
  &redirect_uri=http://localhost:33418/callback
  &scope=openid%20api%3Aread
  &state=<随机>
  &code_challenge=<BASE64URL(SHA256(verifier))>
  &code_challenge_method=S256
  &resource=https%3A%2F%2Fmcp.example.com%2Fmcp
```

```
POST /connect/token
  grant_type=authorization_code
  &client_id=mcp-demo
  &code=<授权码>
  &redirect_uri=http://localhost:33418/callback
  &code_verifier=<verifier>
  &resource=https%3A%2F%2Fmcp.example.com%2Fmcp
```

令牌请求里的 `resource` 可以省略 —— 授权码本身已经记住了受众。但客户端在两处都发更稳妥，
有些授权服务器会校验一致性。

## 4. 令牌长什么样（实测）

```json
{
  "iss": "https://authhub.example.com/",
  "aud": "https://mcp.example.com/mcp",
  "sub": "<用户 ID>",
  "scope": "openid api:read",
  "authhub:permission": ["..."]
}
```

三条值得注意：

1. **`aud` 只有那一个资源**，不含 `authhub-api`。这是有意收窄：如果顺带把 AuthHub 的 API 也塞进受众，
   一张发给 MCP Server 的票同时就是 AuthHub 管理接口的通行证 —— 攻破 MCP Server 就等于拿到用户的全部权限。
   实现见 `AuthorizationController.AttachScopesAndDestinationsAsync`：请求里声明了 `resource` 就**不再叠加** Scope 资源。
2. **不带 `resource` 的老客户端行为不变**：`aud` 仍是 `authhub-api`（按 Scope 的 `Resources` 算）。
3. **刷新令牌保持原受众**：`refresh_token` 换出来的新访问令牌 `aud` 与初次一致。

`iss` 参数也已具备：授权响应里会带 `iss`（RFC 9207），发现文档的
`authorization_response_iss_parameter_supported` 为 `true`，这是 OpenIddict 的默认行为，无需配置。

## 5. MCP Server 侧必须做的三件事

AuthHub 只能保证"票是发给你的"，剩下的在 RS 侧：

1. **发布受保护资源元数据（RFC 9728）**，告诉客户端去哪拿令牌：

   ```json
   // GET https://mcp.example.com/.well-known/oauth-protected-resource
   {
     "resource": "https://mcp.example.com/mcp",
     "authorization_servers": ["https://authhub.example.com/"],
     "scopes_supported": ["api:read"],
     "bearer_methods_supported": ["header"]
   }
   ```

2. **校验 `aud` 含自己**，并且 `iss` 与 `exp` 都合格。只验签名不验 `aud`，等于把"受众绑定"
   这层防护白送掉。未通过时返回 `401` + `WWW-Authenticate: Bearer resource_metadata="..."`。

3. **不要转发令牌**（token passthrough）：MCP Server 不能把收到的令牌原样传给下游 API 当自己的凭证。
   需要调别的服务就用客户端凭证流程另取一张。

## 6. 排错表

| 现象 | 原因 | 处理 |
|---|---|---|
| `invalid_target`（ID2190） | `resource` 没登记（或登记已停用） | 经 `POST /api/resources` 登记，或把停用的登记重新启用 |
| `invalid_request`（ID2192）"not allowed to use the specified resource(s)" | 资源已登记，但该客户端没有 `rsrc:<uri>` 权限 | 更新客户端 `resources` |
| `invalid_request`（ID2032）"code_challenge_method is not supported" | 用了 `plain` | 改用 S256 |
| `invalid_grant`（authorization code already redeemed） | 同一个 code 换了两次令牌 | 正常的重放防护；重新走授权 |
| 拿到令牌但 MCP Server 401 | RS 没校验 `aud`，或校验了但比对的是别家 URI | 检查 RS 的受众判断与 metadata 里的 `resource` |
| 令牌 `aud` 是 `authhub-api` | 客户端没发 `resource` | 按第 3 节补参数 |

## 7. 自动注册（CIMD）—— 可选的 P1 能力

前三节都建立在"客户端已经登记过"这个前提上。Claude Desktop、Cursor、VS Code 这类通用
MCP 客户端，用户不会为了接入一个服务先去后台建客户端，所以本节解决"客户端自助注册"。

**默认关闭。** 打开它等于让 AuthHub 主动请求外部 URL —— 这是本仓库唯一一处
"由匿名请求驱动出站连接"的入口。默认关闭意味着升级不会悄悄改变攻击面。

### 7.1 为什么选 CIMD 而不是 DCR

| | DCR（RFC 7591） | CIMD |
|---|---|---|
| 做法 | 客户端 `POST /connect/register`，AS 返回 `client_id` | `client_id` 本身是 HTTPS URL，AS 去该 URL 拉元数据 |
| AS 状态 | 每个客户端一条记录，且会持续增长 | 同一 URL 只有一条记录 |
| 主要风险 | 开放注册、客户端数爆炸、`redirect_uri` 滥用 | **SSRF**、依赖第三方可用性 |

MCP 2026-07-28 已把 CIMD 列为首选。本仓库实现的是**懒注册**形态：首次见到某个 URL 时
拉取并落库，之后按 TTL 刷新。

为什么要落库而不是纯无状态：OpenIddict 的整条校验链（回调地址匹配、scope 权限、PKCE 要求）
都读库里的应用记录。纯无状态意味着替换 `IOpenIddictApplicationManager` 或绕过全部内置校验 ——
那是把安全边界交给自研代码。懒注册同时保住了内置校验和"记录不随请求无限增长"。

### 7.2 开启

```jsonc
"AuthHub": {
  "Cimd": {
    "Enabled": true,
    "RefreshIntervalHours": 24,      // 元数据缓存有效期；到期重取，失败则降级用缓存
    "FetchTimeoutSeconds": 5,        // 覆盖全部重定向跳的总预算
    "MaxRedirects": 3,
    "MaxMetadataBytes": 65536,
    "AllowedScopes": [ "openid", "profile", "email", "roles" ],
    "AllowedHosts": [],              // 过渡期收窄 SSRF 面：只允许这些 Host
    "AllowedResources": [],          // 空 = 沿用 AuthHub:Resources 全集
    "RequireConsent": true
  }
}
```

`AllowedScopes` 刻意不含 `api:read` / `api:write` / `admin`：那是 AuthHub 自身 API 的授权范围，
不该由一台外部 MCP 服务器替用户决定。

### 7.3 元数据文档要求

`client_id` 必须是一个 `https://` 地址（443 端口、无凭据、无片段）。该地址返回的 JSON 需要满足：

| 字段 | 要求 |
|---|---|
| `client_id` | 必填，且必须**逐字符等于**请求用的 URL |
| `client_name` | 必填。同意页要展示"是哪个应用在要权限"，缺了等于没有知情同意 |
| `redirect_uris` | 必填、非空、绝对地址、无片段；非回环地址必须是 `https` |
| `application_type` | `web`（缺省）或 `native` |
| `token_endpoint_auth_method` | 只能是 `none` —— CIMD 没有登记密钥的环节，客户端一律按 public 处理 |
| `grant_types` | 只允许 `authorization_code` / `refresh_token`，且必须含前者 |
| `scope` | 与 `AllowedScopes` 取**交集**；声明了但一个都不在白名单内 → 拒绝 |

`application_type` 的两种取值直接决定回调地址的放行规则：

- **native** 允许 `http://localhost` / `http://127.0.0.1` / `[::1]`，且入库时**去掉端口**。
  RFC 8252 §7.3 要求忽略 loopback 端口，而 OpenIddict 的 native relaxed 比较只在
  "请求带非默认端口、库里是默认端口"时生效 —— 存了 `:33418` 就永远匹配不上桌面客户端
  下次启动时的新端口。
- **web** 不允许回环地址：一个"网页应用"把回调指向用户本机，等于借浏览器打通本地服务。

### 7.4 注册出来的客户端长什么样

| 属性 | 值 | 为什么 |
|---|---|---|
| `client_type` | `public` | 没有密钥可登记 |
| `consent_type` | `explicit` | 没人审核过它，用户必须看见"谁在要什么"（`RequireConsent`） |
| 权限 | `ept:authorization`、`gt:authorization_code`、`gt:refresh_token`、`rst:code`、`scp:*`、`rsrc:*` | `rsrc:` 缺了的话，客户端一带 `resource` 就会被「不允许使用指定资源」拒掉 —— 第 2 节的能力在这里断掉 |
| 要求 | `ft:pkce` | 公共客户端不带 PKCE，授权码等于明文口令 |
| Properties | `authhub:cimd`、`authhub:cimd:source`、`authhub:cimd:fetched_at`、`authhub:cimd:digest`、`authhub:cimd:disabled` | 管理后台据此区分来源、展示状态、执行禁用 |

### 7.5 SSRF 防护（本能力最高危项）

授权服务器会主动请求客户端提供的 URL，任何一项漏掉，AuthHub 就变成一把能探测内网的梯子。
防护分两层：

1. **URL 层**：仅 `https`、端口 443、无凭据、无片段。
2. **连接层**：每次建连前解析域名，要求**所有**解析结果都是公网地址，并用解析出的 IP 直连
   （`CimdAddressPolicy` + `ConnectPublicOnlyAsync`）。

第二层才是防 DNS rebinding 的关键：只校验 URL 字符串的话，`https://evil.example.com`
可以解析到 `10.0.0.5`。连接层拿的是即将建立连接的那个 IP，"校验后再解析一次"的时间窗不存在。

拒绝的地址段：`0/8`、`10/8`、`100.64/10`、`127/8`、`169.254/16`（云元数据服务就在这里）、
`172.16/12`、`192.0.0/24`、`192.0.2/24`、`192.168/16`、`198.18/15`、`198.51.100/24`、
`203.0.113/24`、`224/4`、`240/4`，IPv6 的 `::`、`::1`、`fc00::/7`、`fe80::/10`、`ff00::/8`、
`2001:db8::/32`、`2002::/16`、`100::/64`、`64:ff9b::/96`，以及全部 IPv4 映射地址。

其他限制：不自动跟随重定向（逐跳重新校验，防止 https→http 降级或跳进内网）、
不解压（防压缩炸弹）、边读边计数（超过 64 KB 直接放弃）、带 Cookie 容器关闭。

### 7.6 缓存、降级与禁用

- **缓存**：有效期内不再出站请求。默认 24 小时。
- **降级**：过期后重取失败（网络故障、第三方挂了、元数据临时不合法）→ 继续用过期的缓存，
  只记一条警告。第三方短暂不可用不该把用户挡在门外。
- **无缓存可降级** → 拒绝，`invalid_client`。
- **禁用**：管理后台的客户端列表里，自动注册的客户端带 `CIMD 自动注册` 徽标，操作列有锁定按钮。
  禁用是**标记**而非删记录 —— 删了之后同一个 `client_id` 下次请求会立刻被重新注册回来，
  删除对自动注册的客户端没有封禁效果。

### 7.7 排错补充

| 现象 | 原因 | 处理 |
|---|---|---|
| `invalid_client` + "元数据缺少 client_name" | 元数据不满足 7.3 | 修客户端的元数据文档 |
| `invalid_client` + "域名 x 解析到非公网地址" | SSRF 防护生效 | 元数据必须托管在纯公网地址上 |
| `invalid_client` + "不在允许自动注册的名单内" | 配了 `AllowedHosts` 但没包含它 | 加白名单或清空该项 |
| 自动注册成功但令牌 `aud` 为空 | `AuthHub:Resources` 没配且库里没登记 | 见第 1 节 |
| 桌面客户端回调被拒 | 元数据声明了 `application_type: web`，或库里存了带端口的 URI | 改成 `native`，并确认入库值无端口 |

## 8. 尚未支持

- **DCR（RFC 7591）**：本仓库只做了 CIMD。若某个客户端只支持 `POST /connect/register`，
  仍需手工登记（或让它改用 CIMD）。
- **资源管理后台页面**：资源的登记/启停目前只有 API（`api/resources`），后台界面未做。
- **AuthHub 自身作为 MCP Server**（第 0 节的 B）：已立项 [#24](https://github.com/ShenHaoCore/AuthHub/issues/24)。
- **CIMD 元数据的多实例共享缓存**：当前缓存就是数据库里的应用记录，多实例天然共享；
  但"到期后并发重取"没有做分布式锁，最坏情况是多拉几次。

