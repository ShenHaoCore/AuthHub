# 分支与协作规范

> 单人项目也需要规则：**规则的作用不是约束别人，而是让三个月后的自己（以及任何读到仓库的人）能一眼看懂当前处于什么状态。**

## 分支模型

```
main      ●─────────────●──────────────●        ← 只接受 develop 的发布 PR，每次合并打 tag
           ╲           ╱ ╲            ╱
develop     ●──●──●──●    ●──●──●──●           ← 集成分支，功能分支的合并目标
             ╲ ╱ ╲ ╱          ╲ ╱
feat/*        ●   ●            ●               ← 短命分支，PR 合并后自动删除
```

| 分支 | 角色 | 谁能合进来 | 生命周期 |
|---|---|---|---|
| `main` | **生产就绪**。任何 commit 都应可发布 | 只接受 `develop` 的 PR（紧急情况见下方 hotfix） | 永久 |
| `develop` | **集成**。功能在这里汇合、跑全量 CI | 只接受 `feat/` `fix/` `chore/` 等的 PR | 永久 |
| `feat/*` | 新功能 | — | 合并即删 |
| `fix/*` | 缺陷修复（非紧急） | — | 合并即删 |
| `chore/*` | 依赖、CI、文档、重构等非功能性改动 | — | 合并即删 |
| `hotfix/*` | 线上紧急缺陷，**从 `main` 开** | — | 合并即删，且必须回合 `develop` |

## 命名规则

```
<type>/<短横线描述>
```

`type` 取自 Conventional Commits，只允许这几种：

| type | 用于 | 例子 |
|---|---|---|
| `feat` | 新增能力 | `feat/totp-recovery-codes` |
| `fix` | 修缺陷 | `fix/refresh-token-replay-window` |
| `chore` | 依赖升级、CI、构建、文档 | `chore/net10-upgrade` |
| `refactor` | 不改行为的结构调整 | `refactor/split-token-service` |
| `docs` | 纯文档 | `docs/deployment-notes` |
| `test` | 纯测试补充 | `test/mfa-channel-coverage` |

要求：

- 全小写、用短横线分词，不出现中文、下划线、大写字母
- 描述**说清做什么**，不要 `fix/bug`、`feat/update` 这种无信息量的名字
- 一个分支只做一件事。混合改动会让 review 和回滚都变难

## 提交信息

遵循 [Conventional Commits](https://www.conventionalcommits.org/)，与分支 type 对齐：

```
<type>(<可选 scope>): <祈使句描述>

<可选正文：为什么改、权衡了什么>
```

实际例子（本仓库历史）：

```
chore: 升级到 .NET 10（LTS）与 OpenIddict 7.7.2
fix(build): 显式引用 System.Security.Cryptography.Pkcs 10.0.13
ci: SDK 固定到 10.0.x（net10.0 无法用 9.x SDK 编译，NETSDK1045）
```

避免 `update`、`修复一下`、`1` 这类提交信息——**提交历史是给人读的，不是给 git 读的**。

## 合并方式

| 方向 | 方式 | 理由 |
|---|---|---|
| `feat|fix|chore/*` → `develop` | **Squash** | 一个功能在 `develop` 上留一个 commit，历史干净、回滚 = 回滚一个 commit |
| `develop` → `main` | **Merge commit** | 保留「这一批功能是作为一次发布进来的」这个事实，tag 打在 merge commit 上 |

PR 合并时 GitHub 会自动删除 head 分支（仓库已开启 `delete_branch_on_merge`），不需要手工清理。

## 发布流程

```bash
# 1. develop 上 CI 全绿后，开 PR：develop → main
# 2. 合并（merge commit），然后打 annotated tag
git checkout main && git pull --ff-only
git tag -a v1.2.0 -m "v1.2.0: <一句话说明>"
git push origin v1.2.0
# 3. 在 GitHub Releases 里基于该 tag 写发布说明（必须写「升级到本版本要做什么」）
# 4. 把 main 回合到 develop，保持两边不漂移
```

**版本号**（语义化）：破坏性变更 → major；向后兼容的新能力 → minor；仅修复 → patch。
数据库迁移属于破坏性变更的强信号，走 major。

## 紧急修复（hotfix）

线上出问题时不走 `develop`：

```bash
git checkout main && git checkout -b hotfix/<描述>
# 修复、提交、开 PR 到 main（CI 必须绿）
# 合并后：立刻把 main 回合 develop，否则下次发布会把 hotfix 冲掉
```

## 分支保护

| 规则 | `main` | `develop` |
|---|---|---|
| 禁止直接 push | ✅ 只走 PR | ✅ 只走 PR |
| 禁止 force push | ✅ | ✅ |
| 禁止删除分支 | ✅ | ✅ |
| 合并前 CI 必须绿 | 编译与测试 / 镜像构建校验 / 依赖漏洞检查 / 静态分析 | 编译与测试 |
| 要求分支与目标同步（up-to-date） | ✅ | ❌（避免多个 PR 互相阻塞） |
| 要求人工 approve | ❌（单人项目无法自我 approve） | ❌ |

> 分支保护是**兜底**，不是替代自觉。它拦住的是「凌晨三点着急修 bug 直接 push main」这类失误。

## 日常命令

```bash
# 删掉远端已删除分支的本地追踪引用（每周做一次，避免 `git branch` 越列越长）
git fetch --prune

# 查看哪些本地分支已合并进 develop，可以安全删除
git branch --merged develop

# 批量清理已合并的本地分支（保留 main / develop）
git branch --merged develop | grep -v -E '^\*|main|develop' | xargs -r git branch -d
```

## 反模式清单

| 做法 | 为什么不行 |
|---|---|
| 长期不合并的功能分支 | 合并冲突随时间指数增长，最后只能推倒重来 |
| 一个分支跨多个需求 | review 无法聚焦，出问题无法只回滚其中一个 |
| 直接 push `main` | 绕过了 CI，`main` 的「可发布」承诺失效 |
| 用 `master` / `dev` / `development` 等别名 | 命名不统一 = 每个新项目都要重新猜 |
| 依赖分支长期堆积不处理 | 本仓库曾积压 10 个 Dependabot PR（其中 3 个升同一个包），基线停在旧版本，全部作废 |
