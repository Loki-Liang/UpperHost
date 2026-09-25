# UpperHost AI 开发总控规则

[English](AGENTS.md) | 简体中文

本文件是 UpperHost 仓库所有 AI 辅助开发的总控规则。任何工具专用规则只能补充细节，不能削弱或绕过本文件。

## 默认开发执行器

对于常规的功能开发、缺陷修复、重构、测试、文档和架构实现，**UpperHost 默认由 OpenHands 执行实际开发**。

其他助手或调度工具可以读取 Issue、规划任务、Review Diff、检查 CI、协调交付；生产代码实现默认应交给 OpenHands。只有以下情况允许使用其他执行器：

- OpenHands 当前不可用或无法访问仓库；
- 任务本身是在修复 OpenHands 接入/治理规则；
- 某项仓库管理动作无法由 OpenHands 完成。

使用备用执行器时，仍必须遵守同一套开发、测试和合并规则，并在 PR 中说明原因。

## 强制交付流程

```text
最新 main + Issue/Task
        |
        v
OpenHands 实现
        |
        v
生产代码 + 对应测试/文档
        |
        v
Focused Validation
        |
        v
.openhands/pre-commit.sh
        |
        v
Pull Request
        |
        v
GitHub Actions Windows 门禁
        |
        v
Diff / 架构 / 测试 Review
        |
        v
Squash Merge 到 main
```

## CURRENT FACTS 规则

每一轮开发或修复开始前，都必须重新从 GitHub 读取当前事实：

- 最新 `main` SHA；
- Issue 验收条件和依赖；
- 已有 PR 时的 base/head/exact-head SHA；
- 当前 Diff 和 Review Threads；
- 当前 CI run/job/step 状态和首个可行动失败。

禁止复用过期 CI 结论或旧分支假设继续开发。

## 分支与合并规则

1. 常规开发禁止直接修改 `main`。
2. 必须从最新 `main` 创建短生命周期分支。
3. 每个分支/PR 保持单一、可审查的交付主题。
4. 生产实现、对应测试和必要文档必须一起推进。
5. 只有当前 exact-head 的 required CI 通过且未解决 Review Threads 清零后才能合并。
6. 默认使用 squash merge，保持 `main` 历史以交付为单位。
7. 禁止“认为已经合并”；必须核对最终 commit 真实存在于 `main`。

## 失败处理规则

1. 先修复首个可行动失败。
2. 修复后，在工具支持时从失败点继续验证。
3. 禁止反复从头执行已经通过的高成本验证。
4. 所有可行动失败关闭后，再执行一次完整 required gate。
5. 禁止通过反复 rerun 掩盖确定性的生产代码、测试、脚本、Workflow 或环境契约问题。

## 测试与文档规则

- 生产行为变化必须补与风险匹配的自动化测试。
- 先跑受影响模块的 focused tests，再跑仓库级门禁。
- 已存在中英文成对文档时必须同步更新。
- 公共架构边界或扩展方式变化时必须更新架构/扩展文档。
- 验收条件要求测试或文档时，只有代码完成不能视为任务闭环。

## 架构硬约束

1. Core 必须保持行业无关。
2. Device 使用 Capability Composition，禁止建立巨型设备继承树。
3. Transport 负责传输机制；Protocol 负责帧和语义解析。
4. 有序字节使用 `ITransport`；离散 Message/Frame 使用 `IMessageTransport<TMessage>`；厂商 SDK 已提供领域 API 时直接实现 Device Capability。
5. Request/Response、Message/Frame、Streaming 必须保持明确边界。
6. Vendor/Native 依赖只存在于 Provider 包，禁止进入 `UpperHost.Abstractions`。
7. UI/Workflow 只能调用 Application/Device Capability，禁止直接访问 Socket 或 Native SDK。
8. Streaming 的 Backpressure/Loss Policy 必须显式。
9. WPF 只是 Adapter，不进入 Core。
10. 软件 Guard/Interlock 不得宣称替代认证硬件安全机制。

## 验证权威

OpenHands 通常运行在 Linux Sandbox，提交 PR 前使用 `.openhands/setup.sh` 和 `.openhands/pre-commit.sh` 做快速验证。

UpperHost 包含 WPF/Windows Target，因此最终发布/集成权威门禁仍是 GitHub Actions Windows CI。禁止把 Linux-only 通过描述成 Windows Runtime/UI 已验证。

## 密钥

禁止提交 OpenHands 凭据、模型 API Key、PAT、签名材料、设备密钥或机器本地配置。
