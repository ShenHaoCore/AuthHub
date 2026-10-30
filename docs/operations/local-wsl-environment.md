# 本机开发环境记录(WSL2 部署)

> 记录时间:2026-10-24 · 环境:Windows 11 + WSL2 Ubuntu 24.04(NAT 模式)
> 2026-10-25 更新:部署形态改为 **Release 发布产物 + FHS 标准布局**(`/opt` 版本化产物、
> `/etc` 配置、`/var/lib` 密钥环),源码不再驻留 WSL(见第 1、2、6 节)。
> 2026-10-25 再更新:部署产物改由 CI 构建并挂到 GitHub Release,WSL 侧只下载不构建,
> 与任何机器上的源码路径解耦(见第 6 节)。
> 本文描述 AuthHub 在本机 WSL 中的部署形态、配套基础设施与运维/排障要点。

## 1. 服务清单

| 组件 | 版本 | 端口 | 管理方式 |
|---|---|---|---|
| AuthHub.Api | net10.0(运行于 .NET SDK 10.0.113) | https://localhost:5001(HTTP 5000) | systemd 用户服务 `authhub` |
| Redis | 7.0.15 | 6379 | 系统服务 `redis-server` |
| PostgreSQL | 16.15 | 5432 | 系统服务 `postgresql` |

- 部署布局(FHS 运维标准):

  | 路径 | 内容 |
  |---|---|
  | `/opt/authhub/releases/vX.Y.Z/` | 每个版本一份完整的 Release 发布产物(framework-dependent,约 126MB) |
  | `/opt/authhub/current` | 软链,指向当前在用的版本 —— **切版本/回滚只改这一条链** |
  | `/etc/authhub/authhub.env` | 全部环境配置(root:ubuntu,640),与服务单元分离 |
  | `/var/lib/authhub/keys/` | Data Protection 密钥环(令牌/会话签名密钥,已从 `~/.aspnet` 迁入) |

  源码克隆已于 2026-10-25 移除,更新与回滚流程见第 6 节(产物从 GitHub Release 下载)。
  (PostgreSQL 支持、Redis 令牌缓存均已合入;Redis 缓存已开启,开关在 `/etc/authhub/authhub.env` 的 `AuthHub__Redis__Enabled`)
- 数据库:PostgreSQL 模式(`Database:Provider=Postgres`,启动时 EnsureCreated + 种子数据,不走 EF 迁移;
  见 [deployment.md · PostgreSQL](deployment.md#postgresql))。
  旧 SQLite 库已于 2026-10-24 确认废弃并删除
- 种子账号:`admin / Admin@12345`(Administrator)、`alice / Alice@12345`(已于 2026-10-24 在 PG 里重新种出)
- OpenAPI:`https://localhost:5001/openapi/v1.json`

## 2. AuthHub 服务运维

```bash
systemctl --user status authhub      # 状态
systemctl --user restart authhub     # 重启
journalctl --user -u authhub -f      # 跟日志
```

服务单元:`~/.config/systemd/user/authhub.service`,只保留运行语义(配置全部在
`/etc/authhub/authhub.env`,由 `EnvironmentFile=` 引入):

```ini
[Service]
EnvironmentFile=/etc/authhub/authhub.env
WorkingDirectory=/opt/authhub/current
ExecStart=/usr/bin/dotnet AuthHub.Api.dll
Restart=on-failure
RestartSec=3
```

`/etc/authhub/authhub.env` 当前内容(改配置改这里,然后 `systemctl --user restart authhub`):

```ini
ASPNETCORE_ENVIRONMENT=Development
Database__Provider=Postgres
ConnectionStrings__DefaultConnection=Host=localhost;Database=authhub;Username=authhub;Password=123
AuthHub__Seeding__MigrateOnStartup=true
AuthHub__Seeding__Enabled=true
AuthHub__Redis__Enabled=true
AuthHub__Security__DataProtectionKeysPath=/var/lib/authhub/keys
```

- Redis 令牌条目缓存(v1.4.0 起可用)已于 2026-10-24 开启:带 Bearer 的 API 请求
  令牌条目查 Redis(键形 `authhub:tokens:id:*`,TTL 默认 60 分钟),撤销仍即时生效;
  Redis 故障自动降级直查 PG。后台页面走 Cookie 会话,不产生缓存键属正常。
- 切回 SQLite(回退):把 env 文件里 `Database__Provider` 改回 `Sqlite`、连接串改回
  `Data Source=authhub.db`,并去掉两行 Seeding 变量即可(旧库已删除,回退后从空库
  重新 EnsureCreated + 种子)
- **配置演进提示**:单元内 `Environment=` 的值含空格必须整体加引号,否则被按空格拆分
  (踩过:连接串被截断为 `Data`,SQLite 解析崩溃);改用 EnvironmentFile 后该坑不再适用 ——
  env 文件里**不加引号**,值按行原样读取。历史教训保留在此,防止有人把配置搬回单元时重蹈覆辙

## 3. WSL 保活(重要)

WSL2 虚拟机默认空闲约 60 秒自动关机,所有服务随之失联,表现为 `localhost:5001` 拒绝连接。已做三层防护:

1. Windows 侧 `%UserProfile%\.wslconfig` 设 `vmIdleTimeout=86400000`(WSL 2.4.10 实测未生效,保留兜底)
2. Windows 启动文件夹 `wsl-autostart.vbs`:登录时启动隐藏会话 `wsl -d Ubuntu-24.04 -u ubuntu --exec sleep infinity` —— **保活会话不断开,虚拟机永不回收(实测有效)**
3. systemd 服务 enabled + `loginctl enable-linger`:WSL 醒来即自动拉起 AuthHub

彻底关 WSL:`wsl --shutdown`(保活会话随之终止,下次登录自动重建)。虚拟机冷启动到服务就绪约 10~20 秒。

## 4. 数据库

### Redis(7.0.15)

- `localhost:6379`,无密码,仅回环监听;配置在 `/etc/redis/redis.conf`(加密码改 `requirepass` 后重启)
- 运维:`sudo systemctl restart redis-server`

### PostgreSQL(16.15)

- `localhost:5432`,TCP 默认 scram-sha-256 **密码认证**;`postgres` 超级用户默认无密码
- 管理入口:`sudo -u postgres psql`(免密);应用连接需先建带密码的账号
- 运维:`sudo systemctl restart postgresql`

应用与测试账号(均为 2026-10-24 创建,仅回环可达的本地开发实例):

| 账号 | 密码 | 属性 | 用途 |
|---|---|---|---|
| `authhub` | `123` | LOGIN,库 `authhub` 的属主 | 应用连接串:`Host=localhost;Database=authhub;Username=authhub;Password=123` |
| `authhub_it` | `l1OaX1l1CQUeq_aQClTy5SKi` | LOGIN CREATEDB | 集成测试 `PostgresDatabaseTests`(建 GUID 命名的临时库,跑完即删);本机跑该用例时设环境变量 `AUTHHUB_TEST_POSTGRES="Host=localhost;Username=authhub_it;Password=...;Include Error Detail=true"` |

> 踩坑:`psql -c "语句1; 语句2"` 把多条语句包在**同一个隐式事务**里执行,
> `CREATE DATABASE cannot run inside a transaction block` 一报错,
> 前面已打印 `CREATE ROLE` 的语句也会被连带回滚 —— 多语句要分成多次 `-c` 跑。
>
> 服务单元已于 2026-10-24 切到 Postgres(三行关键变量见上文第 2 节,启动语义见
> [deployment.md · PostgreSQL](deployment.md#postgresql))。

Windows 与 WSL 两侧均可通过 `localhost:端口` 直连(WSL localhost 转发自动生效)。

## 5. HTTPS 开发证书

- Kestrel 使用 WSL 内的 ASP.NET 开发证书(CN=localhost,有效期至 2027-10-24),
  证书实体在 `~/.dotnet/corefx/cryptography/x509stores/my`(应用直接读证书库,
  不依赖任何文件路径)
- 导出副本统一保管在 `/etc/authhub/certs/`(`authhub-dev-cert.pem` 644 /
  `authhub-dev-cert.key` 640,root:ubuntu)—— Windows 与 Firefox 侧导入用的就是它们
- 已导出并导入 Windows `LocalMachine\Root` 与 `CurrentUser\Root`,Edge/Chrome 显示正常锁标志
- Firefox 使用独立证书库,需手动导入 `/etc/authhub/certs/authhub-dev-cert.pem`
  (Windows 侧 `Downloads\authhub-dev-cert.pem` 是它的另一份拷贝)并勾选"信任此 CA 标识网站"
- **坑**:Root 存储里若残留历史同名的过期 localhost 证书,Edge 会优先匹配到旧证书而报不安全(命令行验证却通过)。已清理 4 张 2022~2024 年残留
- **到期续期(2027-10 前)**:`dotnet dev-certs https` 重新生成 → 导出覆盖
  `/etc/authhub/certs/` 两个文件 → 重新导入 Windows 两个 Root 存储 → Firefox 手动导入,
  全流程即文档这一节

## 6. 代码同步通道

| 通道 | 地址 | 用途 |
|---|---|---|
| origin(WSL,SSH over 443) | `git@github.com:ShenHaoCore/AuthHub.git` | WSL 内 pull/push,密钥 `~/.ssh/id_ed25519`,`~/.ssh/config` 将 github.com 路由到 `ssh.github.com:443` |
| origin(Windows,HTTPS) | 同上 | Windows 侧经 Watt Toolkit 加速使用 |
| mirror(WSL) | `/mnt/g/Dev/Repos/AuthHub` | G 盘开发副本(Windows 侧克隆),不依赖外网;仅开发/排障用,不参与部署 |

- 本网络 GitHub 直连被墙:HTTPS 依赖 Watt Toolkit 的 hosts 加速(**Watt 必须以管理员运行**才能写入 hosts;360 可能拦截 hosts 修改)
- SSH 通道走 `ssh.github.com:443`,天然可达,不依赖 Watt/防火墙;`~/.ssh` 与该通道保留,需要临时克隆排障时仍可用

### WSL 侧更新与回滚(Release 产物形态)

部署产物由 CI 构建并挂到 GitHub Release(工作流 `.github/workflows/release-artifacts.yml`,
Release 发布后自动执行;资产 `authhub-<tag>-linux-fdd.tar.gz` + 同名 `.sha256`)。
WSL 只下载、校验、解压、切链 —— 不碰源码、不做本地构建,与任何机器上的源码路径
无关,换电脑/挪目录/换盘符都不用改流程:

```bash
V=vX.Y.Z    # 目标版本 tag
BASE=https://github.com/ShenHaoCore/AuthHub/releases/download/$V
# 1) 下载产物与校验和并核对(公共仓库,资产下载无需认证)。
#    两个文件必须分开下载:curl「多 URL + -O」在 GitHub 资产的 302 重定向下,
#    第二个文件不会按预期落盘(2026-10-25 实测踩坑),别合并成一条命令
cd /tmp
curl -fLO "$BASE/authhub-$V-linux-fdd.tar.gz"
curl -fLO "$BASE/authhub-$V-linux-fdd.tar.gz.sha256"
sha256sum -c "authhub-$V-linux-fdd.tar.gz.sha256"
# 2) 解压到版本目录(归档无顶层目录,直接落入)
mkdir -p "/opt/authhub/releases/$V" \
  && tar -xzf "/tmp/authhub-$V-linux-fdd.tar.gz" -C "/opt/authhub/releases/$V"
# 3) 切软链、重启;旧版本目录保留,回滚 = 指回旧目录 + 重启(秒级)
ln -sfn "/opt/authhub/releases/$V" /opt/authhub/current \
  && systemctl --user restart authhub
```

- 产物为 framework-dependent,部署机只需 .NET 10 运行时(本机 WSL 装的是 SDK,已覆盖)
- 网络可达性(2026-10-25 实测):WSL 直连 github.com 与两个资产下载域
  (objects/release-assets.githubusercontent.com)链路均通,本节流程即基于此;
  下载速度约 50~100KB/s,50MB 产物约 10~15 分钟,慢但稳定。
  若哪天再被墙:资产在 Windows 侧下载后拷入 WSL(如 `\\wsl$\Ubuntu-24.04\tmp`),
  其余步骤不变
- G 盘镜像自此只服务开发与排障,不再参与部署

## 7. 踩坑记录

1. **WSL 空闲自动关机**:部署常驻服务必须配保活(见第 3 节),否则"隔一会儿就打不开"
2. **systemd `Environment=` 引号**:值含空格必须整体加引号,否则按空格拆分
3. **Edge 报证书不安全但命令行验证通过**:清理 Root 存储中的历史同名 localhost 证书
4. **360 安全卫士**:拦截 hosts 修改与部分提权操作;Watt Toolkit 写 hosts 需管理员身份
5. **GitHub 直连被墙**:HTTPS 靠 Watt 加速;SSH 走 `ssh.github.com:443` 稳定可达
6. **Git Bash 内置 curl 的 TLS 对 Watt 本地代理有兼容问题**(`HTTP 000`),git 本体与浏览器不受影响,排障时别被误导
7. **Windows 侧 dotnet run / F5 报 AddressInUse**:WSL 部署的 localhost 转发会在
   Windows 侧占住 5000/5001(wslrelay 进程),而 Kestrel:Endpoints 配置端点的优先级
   高于 launchSettings/环境变量,默认档案必然撞端口。并行开发的正确姿势:用
   `dotnet run --launch-profile local-sqlite`(5003/5002,零配置 SQLite + 种子,
   admin / Admin@12345)—— 该档案用 `Kestrel__Endpoints__*__Url` 环境变量覆盖端口
   (环境变量 > 配置文件,两条优先级规则叠加后恰好都成立);部署实例的 5001/5000
   仍由 appsettings.Development.json 的 Kestrel:Endpoints 提供,不受影响
