# 架构

> 从 [README](../../README.md) 拆出。讲的是"这个仓库由哪些部分组成、为什么这么分"。

## 目录树

```
AuthHub.slnx
├── src/
│   ├── AuthHub.Domain/           9 个文件   实体、常量、出厂默认的角色-权限映射（不依赖任何框架）
│   ├── AuthHub.Application/     38 个文件   用例编排、接口、DTO、校验器、Result 体系、
│   │                                        配置绑定与校验（Options/）、
│   │                                        角色权限覆盖的读写端口（IRolePermissionOverrideStore /
│   │                                        IRolePermissionAdminService）、
│   │                                        MFA 下发通道（ITwoFactorChannel + 两个实现）
│   ├── AuthHub.Infrastructure/  32 个文件   EF Core、迁移、OpenIddict 适配、各类实现
│   │                                        （三层叠加的 LayeredRolePermissionMap、
│   │                                        反代信任范围解析 ProxyTrustParser、
│   │                                        环境护栏 EnvironmentGuard）
│   └── AuthHub.Api/             49 个 .cs + 11 个 .cshtml + 3 个 appsettings + 5 个静态资源
│                                            控制器、协议页、管理后台（Razor Pages）、
│                                            视图 TagHelper 组件（TagHelpers/）、
│                                            中间件、服务注册与管道（Extensions/）、
│                                            Program.cs（只保留装配清单）、wwwroot
├── tests/
│   ├── AuthHub.UnitTests/        10 个文件  领域规则、Result、校验器、Option 校验器、AccountService（Moq）、
│   │                                        权限映射三层的叠加语义（用 Fake 覆盖存储，不碰数据库）、
│   │                                        反代信任范围解析 / 种子护栏 / 环境护栏
│   └── AuthHub.IntegrationTests/ 15 个文件  WebApplicationFactory 起真实管道跑 OIDC / 鉴权 / 限流 / 错误契约 /
│   │                                        文档端点 / 角色权限配置与后台编辑 / 部署安全（密钥环与反代）/
│   │                                        环境护栏 / 管理后台
├── docs/                          本 README 之外的深度文档 + Postman 集合
├── .github/workflows/ci.yml                编译 + 单元/集成测试 + 依赖漏洞检查
└── Dockerfile                              多阶段构建，非 root 运行
```

依赖方向：

```
Api ──▶ Application ──▶ Domain
 │                        ▲
 └──▶ Infrastructure ─────┘   （Api → Infrastructure 仅用于 DI 注册，不直接使用其类型）
```

该契约由 `tests/AuthHub.UnitTests/Architecture/LayeringTests.cs` 在 CI 强制：Domain 不引用外层与
Web 栈（Identity.Stores 除外）、Application 不引用 Infrastructure/Api/EF/Mvc/Http、Infrastructure 不引用 Api、
单元测试项目不引用 Api。分层破坏直接表现为测试失败，不靠评审用眼。

## 两类浏览器页面走两条不同的路（这是刻意的，不是历史遗留）

| | 协议页：登录 / 2FA / 同意授权 / 提示 | 管理后台：`/admin/*` |
|---|---|---|
| 产出方式 | `Pages/HtmlPages.cs` 用字符串拼装 HTML | Razor Pages（`Pages/Admin/*.cshtml` + `_AdminLayout.cshtml`） |
| 为什么 | 它们是 OIDC 流程的一环，页面内容全部来自当前授权请求。字符串拼装让"表单字段 ↔ 协议参数"的对应关系一眼可见，集成测试也能直接断言页面上出现了哪些参数与 scope | 8 个页面上百个字段，结构高度相似（工具栏 + 表格 + 模态表单）。手写字符串会变成数千行拼接；视图引擎在这里省下的成本远超它的间接层 |
| 样式 | `wwwroot/css/authhub-auth.css` | `wwwroot/css/authhub.css` |

两者共用同一份设计令牌 `wwwroot/css/authhub-tokens.css`，因此登录页与后台是同一套视觉语言
（深色侧边栏 + 浅色卡片 + 蓝色主色，ABP LeptonX 的版式）。

这个取舍后来被修订过一次（新增了 7 个管理页面后，"协议页也一起上 Razor"的结论不再成立），
过程见 [openiddict-notes.md 第 11 条](openiddict-notes.md#11-为管理后台引入-razor-pages对零视图依赖取舍的修订)。

## 几处有意的分层取舍（不是疏漏，写下来避免后来人"顺手修正"）

- `Domain` 引用了 `Microsoft.Extensions.Identity.Stores`：`ApplicationUser` / `ApplicationRole`
  直接继承 `IdentityUser<Guid>` / `IdentityRole<Guid>`。把 Identity 的泛型基类隔在领域模型之外
  收益很小（Identity 是事实上的框架底座），却要为每个字段再写一层映射。
- `Application` 引用了 `Microsoft.AspNetCore.App` 框架引用：唯一原因是 `SignInManager<TUser>`
  定义在 ASP.NET Core Identity 程序集里（随共享框架分发，不在 Identity.Core 包中），登录会话的
  建立/刷新/登出本质上是账号业务的一部分。`ICurrentUser` 接口定义在 Application、实现放在
  Api（依赖 HttpContext）—— Application 层本身不接触 HttpContext。
- 查询密集型服务（`OpenIddictClientAdminService`、`TokenAdminService` 等）落在 `Infrastructure`：
  它们直接操作 `IOpenIddict*Manager` 与 `DbContext`，放在 Application 只会制造无意义的接口隔层。

其中一条由可测性倒推而来：**单元测试项目不引用 Api**（只引 Application / Domain / Infrastructure），
因此需要被单元测试直接钉住的判定逻辑（反代网段解析、种子决策、环境护栏、本机配置文件的插入位置）
都刻意落在 Infrastructure。