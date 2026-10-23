# 分支与协作规范

> 单人项目也需要规则：**规则的作用不是约束别人，而是让三个月后的自己（以及任何读到仓库的人）能一眼看懂当前处于什么状态。**

## 分支模型

```
main      ●─────────────●──────────────●        ← 只接受 develop 的发布 PR（merge commit），合并后打 tag
           ╲           ╱ ╲            ╱
develop     ●──●──●──●    ●──●──●──●           ← 集成分支，功能分支的合并目标
             ╲ ╱ ╲ ╱          ╲ ╱
feat/*        ●   ●            ●               ← 短命分支，PR 合并后自动删除
```

| 分支 | 角色 | 谁能合进来 | 生命周期 |
|---|---|---|---|
| `main` | **生产就绪**。任何 commit 都应可发布 | 只接受 `develop` 的 PR（紧急情况见下方 hotfix） | 永久 |
| `develop` | **集成**。功能在这里汇合、跑全量 CI | 只接受 `feat/` `fix/` `chore/` `docs/` 等的 PR | 永久 |
| `feat/*` | 新功能 | — | 合并即删 |
| `fix/*` | 缺陷修复（非紧急） | — | 合并即删 |
| `chore/*` | 依赖、CI、重构等非功能性改动 | — | 合并即删 |
| `docs/changelog-<ver>` | 发版前的 CHANGELOG 定版（见发布流程） | — | 合并即删 |
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
| 功能分支（`feat/` `fix/` `chore/` `docs/`）→ `develop` | **Squash** | 一个功能在 `develop` 上留一个 commit，历史干净、回滚 = 回滚一个 commit |
| `develop` → `main` | **Merge commit，禁止 squash** | 保留「这一批功能是作为一次发布进来的」这个事实，tag 打在 merge commit 上 |
| `main` → `develop`（发版后回灌） | **Merge commit** | 把 `main` 上的发布提交并回，两边基线与树保持一致 |

**为什么发版 PR 不能 squash**：squash 会把 merge commit 压成单父提交，`main` 上就不存在「发布边界」了 —— 想回答「这个功能是哪个版本进来的」只能翻 CHANGELOG；而且 tag 会挂在以普通 feat 提交身份存在的 squash 提交上，`git log --first-parent main` 看不出任何发布节奏。回灌 PR 也会退化成「空 diff 但必须走一次」的畸形合并。

> 历史偏差留档：`v1.3.0`（`43031f7`）与 `v1.3.1`（`d8d38ef`）两次发版误用了 squash，`v1.2.0`（`7028f3f`）是正确写法，可对照 `git log -1 --format=%p v1.2.0`。

PR 合并时 GitHub 会自动删除 head 分支（仓库已开启 `delete_branch_on_merge`），不需要手工清理。

## 发布流程

顺序不能换：**先定版，再合并，最后打 tag**。tag 一旦推送就不可逆，所以它必须是流程的最后一步，而不是中间一步。

```bash
# 1. develop 上 CI 全绿，且这一批要发的东西已经全部合入
#    切 docs 分支写 CHANGELOG 定版（版本号、变更清单、升级动作），PR 到 develop
git checkout develop && git pull --ff-only
git checkout -b docs/changelog-v1.4.0
# ……只改 CHANGELOG.md……
# 开 PR → develop（squash），合并后删除分支

# 2. 开 PR：develop → main，合并方式选 Merge commit（不是 squash）
#    合并前 main 要求分支 up-to-date，必要时先在 develop 上同步 main

# 3. 打 tag 前必须核对：本地 HEAD 是否真的等于远端 main
git fetch origin
git rev-parse HEAD          # 期望等于下一行
git rev-parse origin/main
git checkout main && git pull --ff-only
git tag -a v1.4.0 -m "v1.4.0: <一句话说明>"
git push origin v1.4.0

# 4. 在 GitHub Releases 里基于该 tag 写发布说明（必须写「升级到本版本要做什么」）
#    只保留当前版本，旧 Release 删除但保留 tag

# 5. 回灌：开 PR main → develop，Merge commit，避免两边基线漂移
```

**为什么第 3 步要显式核对 HEAD**：`git merge --ff-only origin/main` 在本地 tracking ref 陈旧时会返回「已是最新」并退出 0，看起来一切正常，实际上 tag 打在了旧提交上。`v1.3.1` 首次打 tag 就是这样落到了 `v1.3.0` 的提交（`43031f7`），靠 `git rev-parse v1.3.1^{commit}` 与 `origin/main` 比对才发现。判断「本地已是最新」不能依赖 `--ff-only` 的返回码。

**为什么 CHANGELOG 定版要单独一个 docs 分支，而不是塞进功能分支**：版本号是发版时才确定的事实，不是功能开发的一部分。把定版写在功能分支里，`develop` 上会长期挂着一个「尚未发布」的版本说明；若之后还有功能合入，CHANGELOG 顶部就与实际发布内容不符。`v1.3.0` / `v1.3.1` 是把定版塞进功能 PR 的（`#11` / `#14`），`v1.2.0` 用独立 PR（`#8`，`docs/changelog-v1.2.0`）才是正确做法。

**Release 与 CHANGELOG 的分工**：Releases 上只挂当前版本，历史版本一律以
[`CHANGELOG.md`](CHANGELOG.md) 为准——发布页不适合当版本档案，翻历史要一路滚。

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
| 用 squash 合并发版 PR | `main` 上没有发布边界，tag 挂在普通提交上；回灌退化成空 diff 合并 |
| 在功能分支里写 CHANGELOG 版本号 | `develop` 长期挂着「未发布」的版本说明，后续功能合入后与实际发布内容不符 |
| 打 tag 前不核对 `origin/main` | `--ff-only` 在 tracking ref 陈旧时会假装成功，tag 落在旧提交上；tag 一旦推送就不能改 |
