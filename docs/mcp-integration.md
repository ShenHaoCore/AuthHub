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

## 1. 登记资源（必须，且需要重启）

令牌的 `aud` 白名单来自配置：

```jsonc
// appsettings.json
"AuthHub": {
  "Resources": [ "https://mcp.example.com/mcp" ]
}
```

- 只有登记过的**绝对 URI** 才能出现在授权请求 / 令牌请求的 `resource` 参数里；未登记 → `invalid_target`。
- 非绝对 URI（如 `authhub-api`）会让服务**启动即失败**，并指出是哪一项 —— 这是故意的，配错不该等第一个客户端接入才暴露。
- 内置的 `authhub-api` **不要**写进来：它作为 `aud` 是 Scope 的 `Resources` 带出来的（`AuthHubSeeder`），走的是另一条代码路径。
- **改完必须重启**：OpenIddict 的资源校验集合是启动时快照的，没有运行时增删接口（7.7.2 没有资源实体 / 资源管理器）。

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

> 管理后台的客户端编辑页暂时没有"资源"字段（走 API）；见文末「尚未支持」。

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
| `invalid_target`（ID2190） | `resource` 没在 `AuthHub:Resources` 里登记 | 加配置 + 重启 |
| `invalid_request`（ID2192）"not allowed to use the specified resource(s)" | 资源已登记，但该客户端没有 `rsrc:<uri>` 权限 | 更新客户端 `resources` |
| `invalid_request`（ID2032）"code_challenge_method is not supported" | 用了 `plain` | 改用 S256 |
| `invalid_grant`（authorization code already redeemed） | 同一个 code 换了两次令牌 | 正常的重放防护；重新走授权 |
| 拿到令牌但 MCP Server 401 | RS 没校验 `aud`，或校验了但比对的是别家 URI | 检查 RS 的受众判断与 metadata 里的 `resource` |
| 令牌 `aud` 是 `authhub-api` | 客户端没发 `resource` | 按第 3 节补参数 |

## 7. 尚未支持

- **动态客户端注册**（DCR / CIMD）：现在每个 MCP 客户端都要手工登记，Claude Desktop 这类
  "自动发现 + 自动注册"的客户端接不进来。已拆到 [#6](https://github.com/ShenHaoCore/AuthHub/issues/6)，
  倾向 CIMD（无状态），代价是 SSRF 防护必须做扎实。
- **管理后台的资源字段**：目前只能走 `/api/clients`。补齐后编辑页应能勾选资源，而不是让运维改数据库。
- **AuthHub 自身作为 MCP Server**（上表的 B）：未立项。
