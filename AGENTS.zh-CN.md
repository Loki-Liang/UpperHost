# OpenDeviceStudio AI 开发总控规则

[English](AGENTS.md) | 简体中文

本文件是 OpenDeviceStudio AI 辅助开发的仓库级最高规则。它必须保持“薄”：只负责全局开发流程、规则优先级、规则路由和完成门禁。领域长期规则统一放入执行器无关的 agents/ 目录；OpenHands 可复用分析方法放入 .openhands/skills/。

## 权威与规则优先级

规则按以下顺序生效：

1. AGENTS.md：仓库宪法与强制交付流程。
2. 适用的 agents/*.md：执行器无关的领域长期工程规则。
3. 已批准 ADR / 架构决策：项目已经确定的技术决策。
4. 当前 Issue / PR 验收条件：本次交付范围和可量化要求。
5. .openhands/skills/repo.md 等工具执行画像：只能补充执行细节。

低层规则可以更严格，但不能削弱高层规则。发生冲突时必须显式解决，禁止静默选择。

## 默认开发执行器

对于常规功能、缺陷、重构、测试、文档和架构实现，**OpenHands 是 OpenDeviceStudio 默认实际开发执行器**。

其他助手或调度工具可以检查 Issue、规划、Review、排查 CI 和协调交付。只有以下情况允许使用备用执行器：

- OpenHands 不可用或无法访问仓库；
- 任务本身是在修复 OpenHands / AI 治理体系；
- 某项仓库管理动作 OpenHands 无法执行。

使用备用执行器时仍必须遵守完全相同的工程规则，并在 PR 中记录原因。

## 产品定位

OpenDeviceStudio 是**面向工业设备控制、自动化与数据采集的企业级 .NET 上位机开发脚手架**。

项目默认源码直接二开、模块化单体。仓库级工作必须增强可复用 Runtime、Provider、Application Scaffold、Testing、Presentation、Control、Automation 或 Acquisition 能力，禁止把某个具体产品的临时方案塞入共享层。详见 docs/scaffold.zh-CN.md。

## 强制交付流程

所有生产任务按以下顺序执行：

    CURRENT FACTS
      -> 适用规则
      -> 现有架构
      -> 验收 + 证据计划
      -> 实现
      -> Focused Validation
      -> 五审
      -> 整改 Review 发现
      -> Full Required Validation
      -> Pull Request
      -> Exact-Head Required CI
      -> Squash Merge
      -> 核对 commit 真实进入 main

禁止从 Issue 直接跳到写代码。

## CURRENT FACTS

每轮开发或修复开始前重新读取：

- 最新 main SHA；
- 当前 Issue 验收、依赖和交付状态；
- 已有 PR 时的 base/head/exact-head SHA；
- 当前 diff、reviews 和 unresolved review threads；
- 当前 CI run/job/step 与首个可行动失败；
- 相关代码、测试、ADR 和文档。

禁止复用过期 CI 结论和旧分支假设。

## 适用规则路由

所有生产代码变更必须读取 agents/implementation.md、agents/testing.md、agents/review.md、agents/git.md。

涉及以下范围时继续读取：

| 范围 | 强制规则 |
| --- | --- |
| 模块边界、架构、新框架、公共边界 | agents/architecture.md |
| TCP/Serial/USB/BLE、连接与重连 | agents/transport.md |
| 实时采集、DAQ、EMG、Raw、处理、Fan-out | agents/acquisition.md |
| Logging/Metrics/Tracing/Health | agents/observability.md |
| 信任边界、凭据、授权、外部输入 | agents/security.md |
| 打包、部署、Release、Rollback | agents/release.md |
| Public API、配置、源码脚手架兼容 | agents/compatibility.md |

机器可校验的治理清单位于 .github/governance/agent-governance.json。

## 全局工程宪法

- 禁止 Demo、PoC、教程级、只覆盖 Happy Path 的生产实现。
- 优先复用现有能力；新增依赖、框架、抽象或分布式边界必须给出明确技术理由和取舍。
- 有状态行为必须显式定义所有权、生命周期、取消、失败和恢复。
- 共享可变状态必须定义同步或所有权模型。
- Queue、Channel、Buffer、Retry 等潜在增长资源必须有界或给出明确证明。
- 禁止吞异常；基础设施/Vendor 错误在边界转换并保留可诊断上下文。
- Bug 修复在技术可自动化时必须增加能复现原问题的 Regression Test。
- Review 不是报告：发现的 blocker 必须修改代码、测试、文档、Issue 验收或架构后重新审查。
- 没有可观察证据的验收项一律未完成。
- 禁止为了 CI 变绿而削弱测试、兼容性、分支治理或验证。
- 禁止提交凭据、PAT、模型 Key、签名材料、设备 Secret 和机器本地配置。

## Definition of Done

生产任务只有在所有适用项满足后才允许称为完成：

- 架构与所有权边界明确；
- 生产实现完成；
- 失败、取消、停止和恢复行为明确；
- 可观测性能够诊断声明支持的故障；
- 验收条件已映射到可执行/可重复证据；
- 风险匹配的自动化测试通过；
- 适用的 fault/contract/performance 验证通过；
- 兼容性和迁移影响已处理；
- 必要文档已同步；
- 五审无未关闭 blocker；
- exact-head required CI 通过；
- unresolved review threads = 0；
- 最终 merge commit 已核对真实进入 main。

## 验证权威

OpenHands 通常运行在 Linux Sandbox，提交 PR 前运行 .openhands/setup.sh 和 .openhands/pre-commit.sh。

OpenDeviceStudio 包含 Windows/WPF Target，因此 Windows GitHub Actions 仍是 Windows Runtime、WPF、Package 和 Source Scaffold 的权威集成/发布门禁。禁止把 Linux-only 通过描述成 Windows Runtime/UI 已验证。
