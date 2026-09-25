# main 分支治理

简体中文 | [English](main-branch-governance.md)

本文档是 Issue #31 的仓库侧治理合同。UpperHost 除 CI 与 AGENTS 规则外，还必须依赖 GitHub 侧真实启用的 branch protection/ruleset。仓库工作流通过不等于 main 已受到保护。

## Canonical required checks

权威 required check job ID 定义在
`.github/governance/main-branch-policy.json`：

- `openhands-config`
- `architecture-governance`
- `linux-runtime-contract`
- `build-test-template`

`scripts/validate_main_governance.py` 在 `openhands-config` 中执行；如果合同与
`.github/workflows/ci.yml` 漂移，CI 必须失败。

禁止单独重命名或删除上述 job。修改 required check 名称时，必须在同一交付中同步 policy 文件与 GitHub 已启用的 ruleset。

## GitHub ruleset 必须满足

建立一个 Active、目标为 `main` 的 branch ruleset，语义至少包括：

1. 所有修改通过 Pull Request 进入。
2. 要求上述全部 canonical status checks 成功。
3. 合并前分支必须基于最新 `main`（strict required status checks / freshness）。
4. 合并前必须解决 review conversation。
5. 禁止 force push。
6. 禁止删除 main。
7. 普通路径禁止直接 push main。
8. 原则上不设置 bypass actor；如确需管理员紧急 bypass，必须明确且可审计。

单维护者仓库不需要为了形式强行设置审批人数；不可绕过 CI、freshness 和 review thread 才是本 Issue 的基线。

## Exact-head 合并规则

每次真正合并前必须重新读取 PR 并确认：

- base 为 `main`；
- required checks 通过的 SHA 就是当前 head SHA；
- 满足最新 main/freshness；
- mergeability 未被阻塞；
- required checks 全部成功；
- unresolved review threads 为 0。

合并后必须确认最终 merge/squash commit 已进入 `main`，禁止复用旧 head 的 CI 结论。

## 管理权限边界

仓库文件本身不能激活 GitHub ruleset；ruleset 属于 GitHub 仓库管理设置。部分自动化环境的 GitHub 集成拥有 contents/actions 权限，但没有 repository administration 权限。

如果 ruleset/branch protection API 返回 `Resource not accessible by integration`，不得宣称 #31 已完成。必须使用有授权的 GitHub 管理员上下文应用设置，再通过 ruleset API/UI 复核。

## #31 关闭前必须保留的验证证据

- Active ruleset 确实命中 `main`。
- 四个 canonical required checks 已配置。
- strict/freshness 已启用。
- required check 失败的 PR 无法合并。
- required check 缺失的 PR 无法合并。
- direct push / force push / deletion 行为与 policy 一致。
- 文档中的 check 名称与当前 workflow job ID 一致。

后续 #42 External Consumer E2E、#41 Public API Compatibility 等变成正式 Release Gate 后，必须同步加入 policy 与 GitHub ruleset。
