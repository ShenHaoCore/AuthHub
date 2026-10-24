# 本机开发环境记录(WSL2 部署)

> 记录时间:2026-10-24 · 环境:Windows 11 + WSL2 Ubuntu 24.04(NAT 模式)
> 本文描述 AuthHub 在本机 WSL 中的部署形态、配套基础设施与运维/排障要点。

## 1. 服务清单

| 组件 | 版本 | 端口 | 管理方式 |
|---|---|---|---|
| AuthHub.Api | net10.0(运行于 .NET SDK 10.0.113) | https://localhost:5001(HTTP 5000) | systemd 用户服务 `authhub` |
| Redis | 7.0.15 | 6379 | 系统服务 `redis-server` |
| PostgreSQL | 16.15 | 5432 | 系统服务 `postgresql` |

- 代码位置:`~/AuthHub`(WSL 内克隆;origin=GitHub,mirror=G 盘仓库),分支 `develop`
  (PostgreSQL 支持、Redis 令牌缓存均已合入;Redis 缓存默认关闭,要开就在服务单元加
  `Environment=AuthHub__Redis__Enabled=true` 后 daemon-reload + restart)
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

服务单元:`~/.config/systemd/user/authhub.service`,关键环境变量:

```ini
Environment=ASPNETCORE_ENVIRONMENT=Development
Environment=Database__Provider=Postgres
Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=authhub;Username=authhub;Password=123"
Environment=AuthHub__Seeding__MigrateOnStartup=true
Environment=AuthHub__Seeding__Enabled=true
```

- 切回 SQLite(回退):改回 `Database__Provider=Sqlite` + `Data Source=authhub.db` 并去掉上面两行 Seeding 变量即可(旧库已删除,回退后从空库重新 EnsureCreated + 种子)
- **systemd 的 `Environment=` 值含空格必须加引号**,否则被按空格拆分(踩过:连接串被截断为 `Data`,SQLite 解析崩溃)

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

- Kestrel 使用 WSL 内的 ASP.NET 开发证书(CN=localhost,有效期至 2027-10-24)
- 已导出并导入 Windows `LocalMachine\Root` 与 `CurrentUser\Root`,Edge/Chrome 显示正常锁标志
- Firefox 使用独立证书库,需手动导入 `C:\Users\Administrator\Downloads\authhub-dev-cert.pem` 并勾选"信任此 CA 标识网站"
- **坑**:Root 存储里若残留历史同名的过期 localhost 证书,Edge 会优先匹配到旧证书而报不安全(命令行验证却通过)。已清理 4 张 2022~2024 年残留;证书到期后重新 `dotnet dev-certs https` → 导出 → 导入即可

## 6. 代码同步通道

| 通道 | 地址 | 用途 |
|---|---|---|
| origin(WSL,SSH over 443) | `git@github.com:ShenHaoCore/AuthHub.git` | WSL 内 pull/push,密钥 `~/.ssh/id_ed25519`,`~/.ssh/config` 将 github.com 路由到 `ssh.github.com:443` |
| origin(Windows,HTTPS) | 同上 | Windows 侧经 Watt Toolkit 加速使用 |
| mirror(WSL) | `/mnt/g/Repos/AuthHub` | G 盘镜像中转,不依赖外网 |

- 本网络 GitHub 直连被墙:HTTPS 依赖 Watt Toolkit 的 hosts 加速(**Watt 必须以管理员运行**才能写入 hosts;360 可能拦截 hosts 修改)
- SSH 通道走 `ssh.github.com:443`,天然可达,不依赖 Watt/防火墙,为 WSL 侧首选

## 7. 踩坑记录

1. **WSL 空闲自动关机**:部署常驻服务必须配保活(见第 3 节),否则"隔一会儿就打不开"
2. **systemd `Environment=` 引号**:值含空格必须整体加引号,否则按空格拆分
3. **Edge 报证书不安全但命令行验证通过**:清理 Root 存储中的历史同名 localhost 证书
4. **360 安全卫士**:拦截 hosts 修改与部分提权操作;Watt Toolkit 写 hosts 需管理员身份
5. **GitHub 直连被墙**:HTTPS 靠 Watt 加速;SSH 走 `ssh.github.com:443` 稳定可达
6. **Git Bash 内置 curl 的 TLS 对 Watt 本地代理有兼容问题**(`HTTP 000`),git 本体与浏览器不受影响,排障时别被误导
