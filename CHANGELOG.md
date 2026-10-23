# 更新日志

本项目遵循[语义化版本](https://semver.org/lang/zh-CN/)：`主版本.次版本.修订号`。
破坏性变更在条目里显式标注 **BREAKING** 并附升级动作，不靠版本号让人猜。

GitHub Releases 只保留当前版本，历史版本的完整说明以本文为准。每个版本对应的 git tag
保留在 Tags 页（`v1.0.0` … `v1.3.2`），可下载当时的源码快照。

| 版本 | 主题 | 日期 | 源码 |
|---|---|---|---|
| [v1.3.2](#v132--分支与发布规范修正) | 分支与发布规范修正 | 2026-10-23 | [`v1.3.2`](https://github.com/ShenHaoCore/AuthHub/tree/v1.3.2) |
| [v1.3.1](#v131--后台资源字段与客户端编辑修复) | 后台资源字段与客户端编辑修复 | 2026-10-22 | [`v1.3.1`](https://github.com/ShenHaoCore/AuthHub/tree/v1.3.1) |
| [v1.3.0](#v130--mcp-客户端自动注册cimd) | MCP 客户端自动注册（CIMD） | 2026-10-22 | [`v1.3.0`](https://github.com/ShenHaoCore/AuthHub/tree/v1.3.0) |
| [v1.2.0](#v120--mcp-授权支持资源指示器与-s256) | MCP 授权支持：资源指示器与 S256 | 2026-10-22 | [`v1.2.0`](https://github.com/ShenHaoCore/AuthHub/tree/v1.2.0) |
| [v1.1.4](#v114--版本记录集中到-changelog) | 版本记录集中到 CHANGELOG | 2026-10-22 | [`v1.1.4`](https://github.com/ShenHaoCore/AuthHub/tree/v1.1.4) |
| [v1.1.3](#v113--依赖升级与-sdk-固定) | 依赖升级与 SDK 固定 | 2026-10-22 | [`v1.1.3`](https://github.com/ShenHaoCore/AuthHub/tree/v1.1.3) |
| [v1.1.2](#v112--依赖升级汇总) | 依赖升级汇总 | 2026-10-22 | [`v1.1.2`](https://github.com/ShenHaoCore/AuthHub/tree/v1.1.2) |
| [v1.1.1](#v111--分支模型治理) | 分支模型治理（`main` + `develop`） | 2026-10-22 | [`v1.1.1`](https://github.com/ShenHaoCore/AuthHub/tree/v1.1.1) |
| [v1.1.0](#v110--升级到-net-10-lts-与-openiddict-772) | 升级到 .NET 10（LTS）与 OpenIddict 7.7.2 | 2026-10-22 | [`v1.1.0`](https://github.com/ShenHaoCore/AuthHub/tree/v1.1.0) |
| [v1.0.0](#v100--首次公开版本) | 首次公开版本 | 2026-10-19 | [`v1.0.0`](https://github.com/ShenHaoCore/AuthHub/tree/v1.0.0) |

`源码` 列指向对应 tag 的完整代码快照，可用于对比或回溯任意历史版本。

---

## v1.3.2 —— 分支与发布规范修正

2026-10-23

| 类型 | 内容 |
|---|---|
| docs | 明确「**发版 PR 禁止 squash**」。squash 会把 merge commit 压成单父提交：`main` 上不再有发布边界，tag 挂在以普通 feat 提交身份存在的 squash 提交上，回灌 PR 退化成「空 diff 但必须走一次」。附自检方法：`git log -1 --format=%p <tag>` 应输出**两个**父提交 |
| docs | 发布流程改为「**先定版 → 再合并 → 最后打 tag**」。原文是「先 tag，再在 CHANGELOG 顶部补该版本」—— 按那个顺序，tag 指向的快照里根本没有本版本说明，这是定版被塞进功能分支的直接原因 |
| docs | 打 tag 前必须 `git fetch` 并用 `rev-parse` 比对 `HEAD` 与 `origin/main`。`git merge --ff-only origin/main` 在本地 tracking ref 陈旧时会返回「已是最新」并**退出 0**，tag 会静默指向旧提交，不报任何错 |
| docs | 分支表补 `docs/changelog-<ver>`（发版前的定版分支）；反模式清单补三条；修正合并方式表格中把竖线写进单元格导致表头错位的问题 |

升级动作：无代码变更，无需改配置。

**若存在 v1.3.2 之前的克隆**：`v1.3.0` 与 `v1.3.1` 的发布提交对象已重建为合并提交（文件内容逐字节不变，与旧提交 `git diff` 为空），旧克隆 `git fetch` 后会看到分叉，请 `git reset --hard origin/main`（或 `origin/develop`）后继续。

测试：无代码改动，沿用 v1.3.1 的 227 单元 + 156 集成。

---

## v1.3.1 —— 后台资源字段与客户端编辑修复

2026-10-22

| 类型 | 内容 |
|---|---|
| feat | **后台资源字段**：新建 / 编辑客户端弹窗增加「允许的资源」（RFC 8707），勾选即落成 `rsrc:` 权限，不必再走 `/api/clients` |
| feat | 白名单里已撤掉的资源**不会**从已持有的客户端上消失：仍以「已不在白名单」标记出现在弹窗里并保持勾选 |
| fix | **编辑 confidential 客户端必定 500**。`UpdateAsync` 用的是新建的空描述符，而 `PopulateAsync(application, descriptor)` 会**无条件**把 `descriptor.ClientSecret`（null）写回实体 —— 紧接着的校验抛 `ValidationException: The client secret cannot be null or empty for a confidential application`。原实现打算在异常后"恢复哈希"，但那行代码永远执行不到 |
| fix | 同一处还吞掉了描述符里的其他字段：`Properties`（CIMD 自动注册的 marker / disabled / source 标记）、`Settings`、`DisplayNames`、`JsonWebKeySet`。改为先 `PopulateAsync(descriptor, application)` 取原记录再改，这些状态原样保留 |
| docs | README 待办勾掉"管理后台补资源字段" |

升级动作：无需改配置。已受影响的只有"编辑过 confidential 客户端"的部署 —— 那些客户端在编辑时失去了密钥（表现为 `client_credentials` / 授权码换令牌时 `invalid_client`），需要在后台轮换一次密钥。

测试：单元 227 + 集成 156 = **383 项全绿**，Release 构建 0 警告 0 错误。

---

## v1.3.0 —— MCP 客户端自动注册（CIMD）

2026-10-22

| 类型 | 内容 |
|---|---|
| feat | **CIMD 自动注册**：`client_id` 是一个 HTTPS 元数据地址时，首次授权请求会拉取元数据并自动登记客户端，无需人工建单。默认关闭（`AuthHub:Cimd:Enabled`） |
| feat | **SSRF 防护**：仅 https/443、禁私网与回环（含 IPv4 映射、NAT64、6to4）、连接期校验解析后的 IP（防 DNS rebinding）、不自动跟随重定向、不解压、响应体上限 64 KB、总超时 5 秒 |
| feat | **`application_type` 区分**：native 允许 loopback 回调并在入库时去掉端口（RFC 8252 §7.3，也让 OpenIddict 的 native relaxed 比较真正生效）；web 拒绝 loopback 与自定义 scheme |
| feat | **scope 白名单**：元数据声明的 scope 与 `AuthHub:Cimd:AllowedScopes` 取交集；自动注册客户端同时获得 `AuthHub:Resources` 的 `rsrc:` 权限，否则接入 MCP 后一带 `resource` 就被拒 |
| feat | **缓存与降级**：元数据按 TTL（默认 24h）缓存；到期重取失败时继续用过期缓存，只有"从未成功拉过"才拒绝 |
| feat | **管理后台**：客户端列表标出 `CIMD 自动注册` 与 `已禁用`，操作列可一键禁用 / 恢复。禁用是标记而非删记录 —— 删了下次请求会立刻重新注册回来 |
| fix | `AuthHub:Resources` 的读取与校验收口到 `AuthHubResourceRegistry`。原来 OpenIddict 注册与 CIMD 授权各读一遍配置，容易演变成"白名单加了资源但自动注册客户端仍被拒" |
| docs | `docs/mcp-integration.md` 新增第 7 节（CIMD 的选型、元数据要求、SSRF 防护清单、排错表）；README 待办改为 DCR |

测试：单元 227 + 集成 151 = **378 项全绿**，Release 构建 0 警告 0 错误。

## v1.2.0 —— MCP 授权支持：资源指示器与 S256

2026-10-22

| 类型 | 内容 |
|---|---|
| feat | **资源指示器（RFC 8707）**：`resource` 参数绑定令牌 `aud`。资源白名单走配置 `AuthHub:Resources`，启动时登记；未登记 → `invalid_target`；客户端需有 `rsrc:<uri>` 权限。`/api/clients` 的创建 / 更新 / 详情增加 `resources` / `allowedResources` |
| fix | **`resource` 之前被"照收不用"**：`AuthorizationController` 只把 Scope 关联的资源写进 principal，请求里的 `resource` 从不进 `aud`。现在声明了 `resource` 就用它，且不再叠加 Scope 资源（受众收窄） |
| change | **PKCE 只留 S256**：`CodeChallengeMethods` 收窄后，发现文档不再声明 `plain`，请求用 `plain` 直接 400。无破坏性影响（示例客户端本就用 S256） |
| docs | 新增 [docs/mcp-integration.md](docs/mcp-integration.md)：三种"支持 MCP"的区别、资源登记、令牌形状、MCP Server 侧校验、排错表 |
| docs | 更正 README / engineering-notes 里"OpenIddict 7.7.2 无 API 能限制 `code_challenge_method`"的结论 —— 属性在 `OpenIddictServerOptions` 上，经 `builder.Configure` 可取 |

**升级动作**：无（不配 `AuthHub:Resources` 时行为与之前完全一致）。
接入 MCP Server 需要：配置资源 → 重启 → 给客户端加 `resources` → 客户端带 `resource` 与 S256。
详见 [docs/mcp-integration.md](docs/mcp-integration.md)。

---

## v1.1.4 —— 版本记录集中到 CHANGELOG

2026-10-22

| 类型 | 内容 |
|---|---|
| docs | 新增本文件，集中收录 v1.0.0 → v1.1.3 的全部版本说明 |
| docs | `README.md` 文档索引挂链；`CONTRIBUTING.md` 发布流程补「打 tag 后同步补 CHANGELOG」一步 |

### 为什么

发布页不适合当版本档案：版本一多就要一路往下滚，而 Releases 只展示当前版本时，
历史说明会散落在各自的 tag 页里。集中到 `CHANGELOG.md` 后一处可查，也便于按
「症状 → 版本」反查。

### 验证

纯文档变更，不涉及代码与依赖；CI 的编译与测试照常校验链接与格式。

---

## v1.1.3 —— 依赖升级与 SDK 固定

2026-10-22

| 类型 | 内容 |
|---|---|
| chore(deps) | FluentValidation + DI 扩展 `11.12.0 → 12.2.0`、coverlet.collector `6.0.4 → 10.1.0`、Serilog.Settings.Configuration `10.0.0 → 10.0.1` |
| chore | 新增 `global.json` 把 SDK 锁在 `10.0`（`rollForward=latestFeature`），README 前置条件同步 |

### 为什么升 FluentValidation 到 12

11 → 12 是主版本跨越，因此没有直接合并自动升级 PR，而是先在本地跑完 Release 构建
+ 全量 292 项测试确认无破坏性影响，再合并。

### 为什么加 global.json

此前 SDK 版本只由 CI 的 `10.0.x` 与本机安装决定：本机装 10.0.4xx、CI 落到 10.0.1xx 时，
差异只会在某台机器上以 NETSDK 错误复现。`rollForward=latestFeature` 表示 10.0 内允许
按 feature band 向上滚动，但不会跨到 11.0。

### 验证

- Release 构建：`0 警告 0 错误`（`-warnaserror`）
- 单元 159 + 集成 133 = **292 项全绿**
- 5 项 CI 检查全通过：编译与测试、依赖漏洞检查、静态分析（csharp）、镜像构建校验、CodeQL

---

## v1.1.2 —— 依赖升级汇总

2026-10-22

**无运行时 / API 变更，无数据库迁移。**

| 依赖 | 变更前 | 变更后 |
|---|---|---|
| `actions/checkout` | 4 | 7 |
| `actions/setup-dotnet` | 4 | 6 |
| `actions/cache` | 4 | 6 |
| `actions/upload-artifact` | 4 | 7 |
| `docker/setup-buildx-action` | 3 | 4 |
| `docker/build-push-action` | 6 | 7 |
| `github/codeql-action` | 3 | 4 |
| `Scalar.AspNetCore` | 2.17.6 | 2.18.2 |
| `coverlet.collector` | 6.0.0 | 6.0.4 |
| `Serilog.AspNetCore` | 8.0.3 | 10.0.1 |
| `Serilog.Settings.Configuration` | 8.0.4 | 10.0.1 |
| `xunit.runner.visualstudio` | 2.8.2 | 4.0.1 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.11.0 |

**故意未升级**

| 依赖 | 原因 |
|---|---|
| `FluentAssertions` | 7.x 起改用 Xceed 商业许可（商用需付费），且 6→8 断言 API 有破坏性变更。已加入 dependabot `ignore`，需要时由人显式决策 |
| `FluentValidation` 11 → 12 | 主版本，破坏性变更需单独评估（已在 v1.1.3 完成） |

### 测试栈的两处配套变更

`xunit.runner.visualstudio` 4.0.1 与 `Microsoft.NET.Test.Sdk` 18.11.0 必须一起升：
前者适配 Microsoft.Testing.Platform，后者提供 .NET 10 下的测试宿主。两者改到测试工程
同一区域会冲突、无法自动 rebase，因此手工合并升级并在同一基线上验证。

### Dependabot 分组策略

原先按 minor/patch 分组、主版本单独开 PR，结果**同一个包会同时出现在组 PR 和单独 PR 里**。
改为 `patterns: ["*"]` 全量分组后，每个 ecosystem 只开一个 PR。主版本风险由 CI 兜底
（`-warnaserror` 构建 + 292 项测试 + 漏洞扫描），不靠拆 PR 体现谨慎。

### 验证

- Release 构建 `-warnaserror`：0 警告 0 错误
- 单元 159 + 集成 133 全部通过
- 发布 PR 的 5 个 check 全绿

---

## v1.1.1 —— 分支模型治理

2026-10-22

**无运行时变更、无 API 变更、无数据库迁移。** 可直接替换，不需要任何迁移步骤。

### 分支模型确立为 `main` + `develop`

治理前分支是「一个 `main` + 一堆 Dependabot 自动分支」，积压 10 个开放 PR，
其中多个在升同一个包，基线停留在 .NET 9 时代，全部作废。

| 分支 | 角色 | 合入方式 |
|---|---|---|
| `main` | 生产就绪，任何 commit 可发布 | 只接受 `develop` 的发布 PR（**merge commit**） |
| `develop` | 集成，功能在此汇合 | 只接受 `feat/` `fix/` `chore/` 的 PR（**squash**） |

远端分支从 10 个收敛到 2 个。完整规则（命名、Conventional Commits、发布流程、
hotfix 与 back-merge、反模式清单）见 [CONTRIBUTING.md](CONTRIBUTING.md)。

### 分支保护（Rulesets）

| | `main` | `develop` |
|---|---|---|
| 禁止删除 / force push | ✅ | ✅ |
| 必须走 PR | ✅（不要求人工 approve —— 单人项目无法自我批准） | ✅ |
| 合并前 CI 必须绿 | 编译与测试、镜像构建校验、依赖漏洞检查、静态分析 | 编译与测试 |
| 要求与目标分支同步 | ✅ | ❌（避免多个 PR 互相阻塞） |

### CI 与依赖更新

- `ci.yml` / `codeql.yml` 触发分支由 `[main]` 扩到 `[main, develop]`：`develop` 保护
  「集成结果可发布」，`main` 保护「发出去的是绿的」
- Dependabot 的 `target-branch` 改为 `develop`：依赖升级先在集成分支跑绿，再随发布 PR 进 `main`
- **忽略 FluentAssertions >= 7**：7.x 起改用 Xceed 商业许可，且 6 → 8 的断言 API 有
  破坏性变更。需要升级时由人显式决策，而不是每周自动重开三个重复 PR

### 同时完成的依赖升级

| Action | 变更 |
|---|---|
| `actions/checkout` | 4 → 7 |
| `actions/setup-dotnet` | 4 → 6 |
| `actions/cache` | 4 → 6 |
| `docker/build-push-action` | 6 → 7 |
| `github/codeql-action` | 3 → 4 |

### 文档修正

README「已完成」章节里混入的多余 `### 待办` 标题已删除——它导致「待办」出现两次、
.NET 10 升级项被归到错误章节。

---

## v1.1.0 —— 升级到 .NET 10（LTS）与 OpenIddict 7.7.2

2026-10-22

**BREAKING**：技术栈整体升级。`net9.0` 的支持在 2026-11-10 结束，认证中心不应继续
跑在无安全补丁的运行时上。

| 组件 | 变更前 | 变更后 |
|---|---|---|
| .NET / ASP.NET Core | 9.0 | **10.0（LTS，支持到 2028-11）** |
| TFM / LangVersion | net9.0 / C# 13 | **net10.0 / C# 14** |
| OpenIddict | 5.8.0 | **7.7.2**（7.x 才有 `net10.0` 专属 TFM） |
| Entity Framework Core | 9.0.20 | 10.0.13 |
| Docker 基础镜像 | 9.0 | 10.0 |
| Microsoft.OpenApi | 1.x | 2.12.2（随 .NET 10 引入） |

### ⚠️ 升级到本版本必须做一件事

**应用数据库迁移**：OpenIddict 7 把 `OpenIddictTokens.Type` 列长从 50 改到 150
（支持 URI 风格的令牌类型）。

```bash
dotnet ef database update -p src/AuthHub.Infrastructure -s src/AuthHub.Api
```

迁移 `UpgradeToOpenIddict7` 只做列加宽，**不会丢数据**；只有回滚（`Down`）时才可能
截断超长值。SQLite 开发路径走 `EnsureCreated`，无需操作。

### 已处理的破坏性变更（编译器与测试逼出来的，不是照文档猜的）

**OpenIddict 7 改名**

- logout 端点 → end-session：`SetEndSessionEndpointUris`、`EnableEndSessionEndpointPassthrough`、
  `Permissions.Endpoints.EndSession`（数据库旧值 `ept:logout` 仍兼容）
- `HasPrompt` → `HasPromptValue`；`Prompts` → `PromptValues`
- `SetUserInfoEndpointUris` / `EnableUserInfoEndpointPassthrough`（注意大写 `I`）

**Microsoft.OpenApi 2.x**

- `Microsoft.OpenApi.Models` 命名空间合并进 `Microsoft.OpenApi`
- `document.SecurityRequirements` → `document.Security`
- 安全方案引用改用 `OpenApiSecuritySchemeReference`（`OpenApiSecurityScheme.Reference` 已移除）
- 顺带修掉一个真实的 500：`document.Components.SecuritySchemes` 运行时可能为 null，
  直接索引赋值会 NRE

**.NET 10 弃用项**

- `Microsoft.AspNetCore.HttpOverrides.IPNetwork` → `System.Net.IPNetwork`（值类型，
  `Prefix` → `BaseAddress`）
- `ForwardedHeadersOptions.KnownNetworks` → `KnownIPNetworks`
- `Microsoft.Extensions.Identity.Core` 等 4 个包已随共享框架自动可用（NU1510），移除显式引用

**依赖安全**

- 显式引用 `System.Security.Cryptography.Pkcs` 10.0.13：传递依赖原本解析到 9.0.11，
  带已知中/高危漏洞（GHSA-2fmv-rv2c-59x7、GHSA-846f-f764-76mx）

### 验证

- Release 构建 `-warnaserror`：**0 警告 0 错误**
- **单元 159 + 集成 133 全部通过**（集成测试覆盖完整 OIDC 授权码 / 客户端凭证 /
  刷新 / SSO / 限流 / 错误契约 / 文档端点）
- GitHub Actions：编译与测试、镜像构建校验、依赖漏洞检查、CodeQL 静态分析 全绿

### 文档

README 技术栈与路线图已同步；`docs/engineering-notes.md` 新增 5.x → 7.x 改名对照表；
`docs/security-design.md`、`docs/openiddict-notes.md` 的版本表述已更正。

> **PKCE `plain` 待办的更正**：原先预期"升到 OpenIddict 6.x 就能直接配置"，
> 实测 7.7.2 仍无该 builder API，仍需走事件处理器方案（已在 README 待办中更新）。

---

## v1.0.0 —— 首次公开版本

2026-10-19

统一认证授权中心（Identity Provider），为多个下游应用提供一套账号体系与登录入口
—— 一处登录，多处通行。

### 核心能力

**认证与协议**

- OAuth 2.0 / OIDC 完整支持：授权码流程（强制 PKCE）、客户端凭证（M2M）、
  刷新令牌轮换 + 重放防护（重放自动撤销整条授权）
- 单点登录（`prompt=none` 静默签发）与全局登出；下游可凭 `jwks_uri` 离线验签，无需回源
- MFA：TOTP（验证器 App）+ 邮箱/短信验证码，多通道架构（新增通道只需实现
  `ITwoFactorChannel` 并注册一行）

**授权**

- RBAC：角色 → 权限映射，权限以 `authhub:permission` 声明写入令牌
- 权限归属三级叠加：出厂默认 / `AuthHub:RolePermissions` 配置基线 / 后台落库覆盖
  （带防自锁护栏与审计）

**管理后台（Razor Pages，7 个页面）**

- 仪表盘、用户、角色与权限、客户端、Scope、审计日志、我的账户
- 客户端密钥哈希存储、支持轮换；public 客户端配置自相矛盾会被校验器拒绝

**安全**

- 令牌撤销与黑名单（未过期 JWT 立即失效）
- 限流、安全响应头、CSP、CSRF 防护
- RFC 7807 统一错误出口

### 工程质量

- **测试**：单元 159 项 + 集成 133 项（WebApplicationFactory 起真实管道跑 OIDC 全流程）
- **CI**：Release 构建 `-warnaserror`（警告即失败）、Dockerfile 构建校验、依赖漏洞扫描、
  CodeQL 静态分析、Dependabot
- **部署**：多阶段 Docker 构建、非 root 运行、启动期环境护栏
- **文档**：[`docs/`](docs/) 专题文档集 —— 架构、配置、安全设计、部署、测试，
  以及两份跨项目可复用的踩坑笔记（OpenIddict 15 处官方文档差异 / ASP.NET Core 框架行为陷阱）

### 数据库

SQL Server（生产）/ SQLite（开发与测试）双提供者，切换只需改配置；
SQLite 走 `EnsureCreated` 不维护独立迁移。

> 已知待办（PKCE `plain` 模式禁用、MFA 真实通道、Redis 令牌缓存等）见
> [README 路线图](README.md#待办与扩展方向)。
