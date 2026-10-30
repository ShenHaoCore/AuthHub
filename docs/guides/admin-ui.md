# 管理后台（UI）

> 从 [README](../../README.md) 拆出。登录后访问 **`/admin`**（开发环境内置账号 `admin` / `Admin@12345`）。
> 版式按 **ABP Framework LeptonX Lite 的实测度量**对齐（取值见 `lepton-x-lite` 的
> `side-menu/layout-bundle.css` 与 `bootstrap-dim.css`）：280px 深色固定侧边栏（`#161616`）、
> 菜单项 36px 定高直角、选中项 `#222` 实底 + 白字、顶栏为"深色壳 + 浅色圆角浮条"，
> 内容列再以 1px `#e7e9ec` 描边包成一张浮卡；
> 内容区 42/28/56 内边距、无描边白卡（8px 圆角、24px 内边距）、主色 `#355dff`。
> 组件度量同源：顶栏 64px、面包屑 `.725rem` + 1px 竖线分隔、页标题 24px/500、
> 卡片标题 1.25em/500（卡片头无下边线）、按钮与输入框统一 40px 高 / 14px 字号 / 8px 圆角、
> 顶栏头像 24px、徽章 4×7 内边距 / 11px 粗体 / 8px 圆角 / 无描边。
> 列表单元格共用一套文字层级：主行 14px/600、次行 12px/灰、正文 14px；
> 等宽（`.ah-cell-mono`）只换字体不换字号，空值一律 `.ah-cell-sub` + `—`，
> 避免各页列表出现 12.5/14px 混排与行高摆动。
> 页面仍是"卡片头放工具栏、卡片体放表格、卡片尾放分页"的形态，
> 危险操作走模态二次确认，成功/失败用右上角轻提示。
> **视觉契约与验收范围**见 [管理后台视觉系统开发规格](../specs/ui-baseline/spec.md)，
> 状态语义与用词规则逐条列在该开发规格与下文的「状态表达规则」中。

## 目录

- [页面清单](#页面清单)
- [权限模型](#权限模型)
- [前端约定（改动前请先读）](#前端约定改动前请先读)
- [视图组件：四个自定义 TagHelper](#视图组件四个自定义-taghelper)
- [几个在后台里被显式处理掉的陷阱](#几个在后台里被显式处理掉的陷阱)

---

## 页面清单

| 页面 | 路径 | 能做什么 |
|------|------|----------|
| 仪表盘 | `/admin` | 用户 / 客户端 / Scope / 角色计数，近 7 天审计事件柱状图，协议端点速查，最近事件流 |
| 用户 | `/admin/users` | 搜索与筛选、新建、编辑资料、分配角色、锁定 / 解锁、强制下线（撤销全部令牌）、删除 |
| 角色与权限 | `/admin/roles` | 角色列表 + 权限矩阵（权限 × 内置角色）、新建角色、改说明、删除；**每个角色的权限树可直接勾选保存，即时生效** |
| 客户端 | `/admin/clients` | 注册 / 编辑 OAuth 客户端、勾选授权类型与 Scope、轮换密钥（明文只显示一次）、删除 |
| Scope | `/admin/scopes` | 新建 / 编辑 / 删除 Scope、维护关联资源（进入令牌 `aud`）、查看被多少客户端引用 |
| 资源 | `/admin/resources` | 登记 / 编辑 / 启停 / 删除 RFC 8707 资源标识（令牌 `aud` 白名单）、查看被多少客户端持有 `rsrc:` 权限 |
| 审计日志 | `/admin/audit-logs` | 按动作 / 用户 / 客户端 / 时间区间查询，快捷范围（今天 / 7 天 / 30 天），折叠查看 User-Agent |
| 我的账户 | `/admin/profile` | 档案与权限总览、修改密码、启用 / 关闭 TOTP 两步验证（含恢复码）、测试邮件 / 短信通道 |

## 权限模型

授权在本项目里分成**三层**，每层的可变性不同（这也是绝大多数授权系统的通用结构）：

| 层 | 是什么 | 载体 | 怎么改 | 需要发版吗 |
|----|--------|------|--------|-----------|
| ① 能力目录 | 有哪些权限点（`clients.manage` …） | `AuthHubConstants.Permissions`（编译期常量） | 加一行常量 + 在端点上挂 `[Authorize]` | **要**（端点是程序集的一部分） |
| ② 授权策略 | 每个角色拥有哪些权限点 | 出厂默认 → `AuthHub:RolePermissions` 配置 → **`RolePermissionOverrides` 表** | 改配置需重启；**后台 `/admin/roles` 勾选即时生效** | **不要** |
| ③ 授权关系 | 谁拥有哪个角色 | Identity 的用户-角色表 | 后台 `/admin/roles`、`/admin/users` | 不要 |

② 层内部又是**三级叠加**，后者覆盖前者：**出厂默认 ← `AuthHub:RolePermissions` 配置 ← 数据库覆盖**。

侧边栏按权限收敛，但**真正的访问控制在服务端策略上**，不依赖前端是否隐藏了入口
（`AuthHubConstants.Policies.Ui.*`，见 `AuthorizationPolicyExtensions.AddUiPermissionPolicy`）：

| 策略 | 要求 |
|------|------|
| `Ui.Authenticated` | 只要登录（`/admin/profile`，普通用户也要能改自己的密码） |
| `Ui.Admin` | 仪表盘：持有 6 个管理权限中的任意一个 |
| `Ui.UsersManage` / `RolesManage` / `ClientsManage` / `ScopesManage` / `TokensRevoke` / `AuditRead` | 对应的 `authhub:permission` 声明（由 `IRolePermissionMap` 从角色展开） |

### ② 层的部署期基线：配置

```jsonc
{
  "AuthHub": {
    "RolePermissions": {
      "Administrator": ["clients.manage", "scopes.manage", "users.manage", "roles.manage", "tokens.revoke", "audit.read"],
      "UserManager":   ["users.manage", "tokens.revoke"],
      "Auditor":       ["audit.read"],
      "User":          []
    }
  }
}
```

- **配置只做覆盖，不做替换**：只写 `"Auditor": [...]` 也能工作，未列出的角色继续用出厂默认。
  因此老部署不写这一段时行为**逐字不变**；配置里 `"X": []` 是有效写法，表示显式收回该角色的全部权限。
- **权限名写错会在启动期直接失败**，不会安静地少给权限：
  ```
  OptionsValidationException: 角色 Auditor 引用了未声明的权限“Users.Manage”。
  合法值：clients.manage、scopes.manage、users.manage、roles.manage、tokens.revoke、audit.read。
  新增权限点属能力目录变更，需要改 AuthHubConstants.Permissions 并重新部署。
  ```
  校验器是 `RolePermissionOptionsValidator`，比对是**大小写敏感**的（`ordinal`）——
  权限名是要写进 `authhub:permission` 声明、再被 `[Authorize(Policy=...)]` 精确匹配的字符串，
  容忍大小写差异等于制造"看起来配上了却没生效"。
- 校验挂在 `IOptions<RolePermissionOptions>` 上，而 Options 本身是**懒加载**的
  （首次求值通常发生在第一个用户登录时）。为了让配置错误落在**启动日志第一屏**，
  `Program.cs` 在 `Build()` 之后显式调了一次 `ValidateAuthHubStartupConfiguration()`。
  这个方法**刻意只取 `IOptions`、不碰数据库** —— 它跑在迁移**之前**，
  若在这里解析需要读库的服务，就会埋下"单例先于数据库初始化被解析"的雷。
- 刻意**不**校验的：角色名是否内置（自定义角色本来就允许配权限）、是否存在"没有任何角色持有"的孤儿权限点。
- **不要**给自定义角色配一条映射就以为它成了系统角色：`IsBuiltInRole` 的判据是编译期的
  `AuthHubConstants.Roles.All`，与配置无关 —— 否则删一次角色反倒把它变成不可删除的。

### ② 层的运行时覆盖：后台勾选，即时生效

`/admin/roles` 里点开任一角色的「权限」，弹出的树是**可勾选、可提交**的。保存落进
`RolePermissionOverrides` 表，**下一位用户登录立刻用新归属签发声明，不需要重启**。

**为什么是一行一角色的完整快照，而不是「角色-权限」关系表**：
关系表里"没有行"同时表示两件语义完全不同的事 —— "这个角色没被覆盖过"（应回落配置）
与"这个角色被显式设成没有任何权限"（不该回落）。用关系表就必然出现
"取消勾选后权限又自己回来了"。快照行天然带这个区分：**有行 = 被覆盖，行内空数组 = 显式收回全部**。
这条语义由单元测试 `Runtime_override_with_empty_list_should_revoke_everything_and_must_not_fall_back` 钉住。

其余几条设计取舍：

- **主键是角色名，不是角色 Id**：策略、声明、配置基线全以角色名为键，用名字免掉一次 join，
  也让"配置里声明了但还没建出来"的角色可以预留覆盖。代价是删角色时要顺手清覆盖行（无外键级联），
  `RoleAdminService.DeleteAsync` 已经这么做了 —— 不然下次建同名角色会"继承"上一任的权限。
- **防自锁**：唯一能把管理员彻底关在门外的改动。保存/恢复前会检查"改完之后是否仍有角色持有 `roles.manage`"，
  否则拒绝并提示先把 `roles.manage` 授给另一个角色。没有这道护栏，勾掉最后一个是不可逆的（只能改数据库）。
- **服务端二次校验**：界面只列目录里的权限，但表单数据一律当不可信输入；权限名用 **ordinal** 比对，
  未声明的名字直接拒绝（理由同配置校验器）。
- **快照缓存 + `Invalidate()`**：`IRolePermissionMap` 在登录热路径上（生成 `authhub:permission` 声明），
  不能每次回库。保存后只失效**本进程**的缓存 —— 多实例部署时其他实例靠缓存自然过期，
  且它们本来也只影响自己的登录签发。**载入是惰性的**：单例可能先于数据库初始化被解析，构造函数里不碰数据访问。
- **两阶段启动顺序**：`ValidateAuthHubStartupConfiguration()`（只取 Options，迁移**前**）
  → `InitializeAuthHubDatabaseAsync()`（迁移）→ `WarmUpAuthHubRolePermissions()`（读库，迁移**后**，打一行
  `角色权限归属已就绪 | 角色数=… | 其中有自定义覆盖=… 个`）。拆成两段是为了让配置错误落在启动日志，
  又不让读库失败拖到第一个用户登录才暴露。
- 每次变更都写审计（`admin.role.permissions.updated` / `admin.role.permissions.reset`，
  正文里带角色名与最终权限清单），并在角色列表上标出「已自定义」。
  **「恢复默认」按钮只在真的被覆盖过时露出**，删掉覆盖行即回落配置 / 出厂默认。
- **声明的时效性**：权限声明是登录那一刻写进会话 Cookie 的。已在线用户最迟在一个安全戳校验周期
  （本项目 5 分钟）后刷新，重新登录立即生效。

> **一个容易踩的坑**：`Extensions/AuthenticationExtensions.cs` 把 `DefaultChallengeScheme`
> 指向了 OpenIddict Validation
> （为的是 `/api/*` 未带令牌时返回 401 而不是 302）。所以后台的 UI 策略**必须显式只挂会话 Cookie 方案**，
> 否则浏览器访问 `/admin` 会拿到 `401 + WWW-Authenticate`，用户看到一片空白而不是登录页。
> 集成测试里有一条用例专门钉住这个行为。

## 前端约定（改动前请先读）

### 状态表达规则

列表里的"状态"只允许四种写法，颜色由**语义**决定，不由页面决定。新增状态列时照这张表选色，
不要为了让某一行显眼而新造色号：

| 语义 | 写法 | 当前用例 |
|------|------|----------|
| 正常 / 已完成 | `.ah-badge--success`（绿） | 用户"正常"、邮箱"已验证"、MFA"已开启"、密钥"已配置"、资源"已启用"、审计"成功"、已授予的 scope / 权限 |
| 待处理 / 临时 / 有风险 | `.ah-badge--warning`（琥珀） | "已锁定"、"未验证"、"未开启"、"CIMD 自动注册"、"已不在白名单"、权限归属被改动 |
| 不可用 / 失败 | `.ah-badge--danger`（红） | 用户 / 客户端 / 资源"已停用"、审计"失败" |
| 分类 / 计数 / 非状态说明 | 裸 `.ah-badge`（灰） | 客户端类型 `public` / `confidential`、授权类型、关联资源、"协议"标记、分组头的"N 项" |

配套约定：

- **身份标签**（用户所属角色、个人页角色列表、"内置"标记）用 `.ah-badge--primary`，与状态色区分开——蓝色不代表"好"。
  角色列表自己的名称列是主行文字（它是那一行的标识，不需要再加一层标签）。
- **灰字不是状态**：`.ah-cell-sub` 只用于次要说明与空值（`—` / "未被引用" / "无权限"）；
  一个 yes/no 的状态必须是徽章，不能一边写徽章、一边写成灰字。
- **计数不是徽章的活**：表格里的计数列用正文文字（引用数："3 个客户端"），为零时用 `.ah-cell-sub` 写"未被引用"；
  只有分组标题旁的数量（"7 项"）才用灰徽章，避免整列都是小色块。
- 状态词统一为"已启用 / 已停用 / 已开启 / 未开启 / 已验证 / 未验证 / 已锁定 / 已配置"，
  同一含义不要在不同页面换词（例：停用不再写"已禁用"）。
- 用户账号状态的三态（正常 / 已锁定 / 已停用）额外带 `.ah-dot` 圆点，作为唯一需要"一眼扫过"的列。

- **CSP 是 `script-src 'self'`**，行内 `<script>` 与 `on*` 事件属性会被浏览器静默拦截。
  因此所有交互都写在 `wwwroot/js/authhub.js` 里，用 `data-*` 属性声明行为：
  `data-dialog-open` / `data-dialog-close`、`data-confirm`、`data-copy`、`data-tab`、`data-menu-toggle`。
  `style-src` 允许 `'unsafe-inline'`，所以行内 `style` 属性与 `<noscript><style>` 可以用。
  **唯一的例外**：`/scalar` 前缀（API 文档 UI）的 `script-src` 额外放行 `'unsafe-inline'` ——
  Scalar 的页面模板自带一段内联初始化脚本，拦掉它页面就是一片空白（返回 200、资源全通，
  纯接口断言发现不了）；.NET 集成拿不到 nonce，放宽范围被中间件限制在这一条路径上，
  且有两条护栏测试钉住"基准策略不得扩散 unsafe-inline"。
- **零外部依赖**：不引用任何 CDN（字体、图标、QR 库都没有）。图标是 `AdminNav.Icons` 里的
  24×24 SVG path 数据，直接内联进标记。集成测试会断言后台页面里不存在站外 `src` / `href`。
- **渐进增强**：脚本被禁用（或被 CSP 拦下）时，`_AdminLayout.cshtml` 里的 `<noscript><style>`
  会隐藏标签条并强制展开全部面板，内容依然可读、表单依然可提交。
- **表单走 PRG**（Post-Redirect-Get）：成功 302 回列表 + `TempData["Success"]` 轻提示；
  失败原地重渲染并用 `data-dialog-autoopen` 重新打开弹窗，保留用户已填的值。
- **一次性机密只出现一次**：客户端明文密钥、MFA 恢复码都用 `TempData`
  （Data Protection 加密、HttpOnly、读取即删）跨重定向展示一次。

## 视图组件：四个自定义 TagHelper

改后台视图前先看一眼 `src/AuthHub.Api/TagHelpers/`。图标与弹窗外壳不手写，
统一走 `<ah-*>` 标签，`Pages/_ViewImports.cshtml` 里用 `@addTagHelper *, AuthHub.Api` 注册。

| 标签 | 产出 | 关键属性 |
|------|------|----------|
| `<ah-icon path size />` | `<svg class="ah-icon" …>` | `path` 取 `AdminNav.Icons.*`；`size` 默认 16 |
| `<ah-dialog id title auto-open size>` | `<dialog class="ah-modal">` + 标题栏 | `auto-open` **按字符串原样透传**；`size="lg"` → `ah-modal-lg` |
| `<ah-dialog-body>` | `<div class="ah-modal-body">` | 无 |
| `<ah-dialog-foot dismiss submit secondary secondary-handler />` | `.ah-modal-foot` + 取消 / 次级 / 提交按钮 | `dismiss` 默认「取消」；不传 `submit` 则只读；`secondary` 渲染成排在主按钮左侧的次级提交按钮，`secondary-handler` 给它 `formaction="?handler=…"`（相对当前文档解析 ⇒ 仍在本页且沿用同一表单，**防伪令牌不会丢**） |

**为什么用 TagHelper 而不是 partial 视图**：弹窗的 head / body / foot 之间夹着任意表单内容，
partial 承载不了子内容；而 TagHelper 能在**编译期**校验属性名——写错属性名直接编译失败，
partial 写错模型属性要等运行期才炸。

**这族类的形态**（`AuthHubTagHelperBase` + 四个子类）是刻意的：基类只提供 `Icon()` / `Button()`
两个绘制原语，四个子类各自覆写 `Process()` 决定怎么消费它。新增一种"重复的 HTML 骨架"时照这个模式加子类，
不要去改基类加 `switch`。

几条不写下来一定会踩的约定：

- **`auto-open` 必须是显式的字符串 `"true"` / `"false"`**。绑成 `bool` 属性 Razor 会渲染成
  `"True"`，而前端按值判断 → 所有弹窗一起弹出来（这是本仓库真实发生过的故障）。
  不传时**整条属性都不输出**，只读弹窗就靠这个。两条集成测试从正反两面钉住它。
- **标题 id 与 `aria-labelledby` 由同一个 `{id}-title` 表达式生成**。别在视图里手写
  `<dialog aria-labelledby=…>`——手写的那个必然与生成器脱钩，弹窗会静默失去无障碍名称。
  一条集成测试会遍历所有弹窗断言这两者互相命中。
- **标题栏被排在 `<form>` 之前**（`PreContent`）。改造前它在 `<form>` 内部，是 `<dialog>` 的唯一子元素。
  两者布局完全等价：`authhub.css` 里 `dialog.ah-modal` **不是** flex 容器，也没有任何
  `>` 子选择器，全 CSS 里唯一涉及 `form` 的规则是 `.ah-menu form { margin: 0 }`；
  关闭按钮是 `type="button"`，放在表单外不影响提交。
- **`data-dialog-close` 会渲染成 `data-dialog-close=""`**。`TagBuilder` 表达不了 HTML 的无值属性；
  两者在 DOM 里是同一个东西（HTML 解析器把无值属性规范化成空串），`authhub.js` 用
  `querySelectorAll('[data-dialog-close]')` 按存在性匹配。
- 底部「取消」按钮在 `<form>` 内部，**必须是 `type="button"`**，否则点它会提交表单并触发校验失败。
  由 `DialogFootTagHelper` 统一保证，并有测试断言。

> 重构这层的安全网是"逐页比对渲染结果"：把视图改动 stash 掉，用同一套集成测试夹具
> 先渲染一份基线，再恢复改动渲染第二份，然后施加一组**已声明的等价改写**
> （svg 属性归一、`data-dialog-close` 归一、标题 id 归一、`<form>` 外壳与隐藏域位置归一），
> 要求两边**逐行完全相等**。剩下任何差异都是没被解释的真回归——比"看一遍 diff 觉得都对"可靠。

## 几个在后台里被显式处理掉的陷阱

| 陷阱 | 处理方式 |
|------|----------|
| 编辑客户端时若只提交数据库里的 scope，会把 `openid` / `offline_access` 静默删掉（它们不落 Scope 表） | 把它们作为"协议 Scope"一并列出参与往返；客户端独有的 scope 也单独成组，保证不会被无声丢掉 |
| 每次保存用户资料都刷新安全戳 → 把用户踢下线 | 只在角色集合**确实变化**时才调 `AssignRolesAsync` |
| 未改动的字段也会被写进审计 → 假审计记录 | `UnchangedAsNull`：与当前值相同的字段传 `null`（服务端的"不修改"语义） |
| 权限树/矩阵在界面上看着"漏做了编辑功能" | 现在可直接勾选保存（落 `RolePermissionOverrides` 表、即时生效）。仍**改不了**的是权限点本身：它挂在端点的 `[Authorize]` 上，属于程序集，运行时改不了，页面文案讲清了这条边界 |
| 用「角色-权限」关系表存覆盖 → "取消勾选后权限又自己回来了" | 表里"没有行"无法区分"没覆盖过"与"显式设成空"，故改用**一行一角色的完整权限快照**：有行=被覆盖，行内空数组=显式收回全部 |
| 改权限归属能把管理员自己锁在门外（勾掉最后一个 `roles.manage`） | `RolePermissionAdminService.EnsureRolesManageSurvives` 在保存/恢复前检查"改完后是否仍有角色持有 `roles.manage`"，否则拒绝并给出补救文案 |
| 配置里的权限名拼错/大小写不符 → 运行时静默少给权限 | `RolePermissionOptionsValidator` 在启动期严格比对 `Permissions.All`（ordinal），写错直接让进程起不来，错误信息里带上角色名、非法值、全部合法值 |
| 按天分桶的审计统计在闭区间下会漏算/重算边界毫秒 | 一天的结束取"次日零点减 1 tick"（仓储用的是 `>=` 与 `<=`） |
| 弹窗 id 里直接嵌了 Scope 名（可能含 `:`，如 `api:read`），`querySelector('#x:y')` 会抛 `SyntaxError` | `authhub.js` 的 `byIdOrSelector` 回退到 `getElementById`（它接收任意 id 字面量、不做解析） |