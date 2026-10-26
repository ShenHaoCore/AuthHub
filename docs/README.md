# 文档索引

> 本目录按**用途**分三类：`guides/`（怎么用）、`reference/`（是什么）、`operations/`（怎么部署与运维），
> 特性级开发规格放在 `specs/`，附件放 `assets/`。根目录的 [README](../README.md) 是项目总览。

## guides/ —— 怎么用

| 文档 | 内容 |
|------|------|
| [管理后台](guides/admin-ui.md) | 页面清单、三层权限模型、前端约定（CSP / 零外部依赖）、`<ah-*>` TagHelper、已处理的陷阱 |
| [API 参考](guides/api-reference.md) | 端点清单与权限要求、错误响应约定（RFC 7807 统一出口） |
| [接入 MCP](guides/mcp-integration.md) | 三种"支持 MCP"的区别、资源登记、`aud` 绑定、MCP Server 侧校验、排错表 |
| [测试与验证](guides/testing.md) | 单元 / 集成覆盖清单、端到端脚本、Postman 集合 |
| [分支管理速览](guides/branch-management.md) | Git Flow 日常操作与发版步骤（完整规范见 [CONTRIBUTING](../CONTRIBUTING.md)） |

## reference/ —— 是什么

| 文档 | 内容 |
|------|------|
| [架构](reference/architecture.md) | 分层结构、两类页面为什么走两条路、有意保留的分层取舍 |
| [环境配置](reference/configuration.md) | 四个配置来源、三份文件分工、启动期环境护栏、`dotnet ef` 的连接串来源 |
| [安全设计](reference/security-design.md) | PKCE、令牌轮换、限流、CSRF、CSP 等逐项设计与理由 |
| [OpenIddict 踩坑](reference/openiddict-notes.md) | 照官方文档写会编译不过 / 运行期才炸的差异清单 |
| [工程笔记](reference/engineering-notes.md) | ASP.NET Core / OpenIddict 的框架行为陷阱，按症状 → 根因 → 修法记录 |

## operations/ —— 怎么部署与运维

| 文档 | 内容 |
|------|------|
| [部署](operations/deployment.md) | 生产配置清单、容器 / 反代的两个隐性依赖、迁移、Docker |
| [本机 WSL 环境](operations/local-wsl-environment.md) | 本机 WSL2 的部署形态、配套基础设施、运维与排障 |

## specs/ —— 特性级开发规格

| 文档 | 内容 |
|------|------|
| [开发规格索引](specs/README.md) | 特性一览与票进度；每个特性一张规格，票按 `Blocked by` 串行领取 |
| [管理后台视觉系统](specs/ui-baseline/spec.md) | LeptonX 基线、列表文字层级、状态语义与用词；三张票在 `issues/` 下（全部完成） |

## assets/

- [Postman 集合](assets/AuthHub.postman_collection.json)：可直接导入，按五个文件夹组织，文件夹之间有令牌传递。

## 新增文档时

1. 先按用途选目录：讲步骤 → `guides/`；讲设计与事实 → `reference/`；讲部署与排障 → `operations/`；
   某个特性的目标与验收 → `specs/<feature-slug>/spec.md`（票放同目录 `issues/`）。
2. 在本文对应表格里加一行，一句话说明它回答什么问题。
3. 文内链接一律用相对路径；跨目录引用时注意层级（`../guides/`、`../../README.md`）。
