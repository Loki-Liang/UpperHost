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

## 产品定位：上位机开发脚手架

UpperHost 是**面向工业设备控制、自动化与数据采集的企业级 .NET 上位机开发脚手架**。仓库的职责是给工业上位机产品开发者提供可复用 Runtime、工程约定、Provider 接缝、测试基础设施、Samples 和项目模板。

UpperHost 不是已经完成的通用 Workbench/HMI，也不是低代码产品。具体产品的 Device 业务语义、私有协议、Control/Interlock 规则、业务 Workflow 和产品 UI 都属于使用脚手架创建的应用工程。

常规 UpperHost 开发中**禁止引入**可视化 Workflow Editor、节点图、拖拽连线编程界面、通用低代码 Flow Engine、可视化 DAG 编辑或从流程图生成业务代码。

`UpperHost.Workflows` 和 `UpperHost.StateMachines` 继续作为代码/API Runtime 模块存在。

详见 `docs/scaffold.zh-CN.md`。

## 架构基线：模块化单体

UpperHost 默认采用**模块化单体（Modular Monolith）**：一个可部署应用/进程，由边界清晰的模块和 Adapter 组合而成。禁止为了“分层”而擅自引入微服务、远程 RPC、重复的服务私有模型或分布式一致性。任何分布式边界都必须有独立 Issue/ADR 和明确的运维收益依据。

### 模块依赖规则

1. `UpperHost.Abstractions` 是稳定依赖根，禁止引用仓库内其他 Project。
2. Control、Protocols、Dataflow、Workflows、StateMachines、Events、Resilience、Diagnostics、Testing 等平台模块必须暴露窄公共契约，禁止依赖 Presentation、Samples、Tests、Templates。
3. `UpperHost.Transport.*`、Storage/Provider 属于基础设施 Adapter，只能向内依赖稳定契约，禁止把 Vendor/Native 概念反向塞进 Core。
4. `UpperHost.Presentation.*` 是最外层 Adapter；任何生产模块禁止反向依赖 Presentation。
5. `UpperHost.Starters` 是 Composition/Convenience 模块；其他生产模块禁止反向依赖 Starters。
6. `samples/`、`tests/`、`templates/` 可以组合生产模块；生产模块绝不能引用它们。
7. 禁止 ProjectReference 环依赖。
8. 跨模块协作必须通过公开 Capability/Contract/Event；禁止访问其他模块内部实现、通过反射绕过边界或建立隐藏静态耦合。
9. 公共 API 必须最小化；除非跨模块真实需要，否则类型默认保持 internal/private。
10. 新建模块必须说明职责、所有权边界、允许依赖和对应测试；禁止为了移动文件而机械拆 Project。

`scripts/validate_architecture.py` 是可执行的 ProjectReference 架构门禁，并进入 pre-commit/CI。

## 工程实现规则

- **DI/组合根：**依赖在 Application/Hosting/Starters 组合根装配；Domain/平台逻辑禁止可变全局单例和 Service Locator。
- **Async/I/O：**I/O 链路全程异步；有取消语义时必须接收 `CancellationToken`；禁止 `.Result`/`.Wait()` 和无人管理的 fire-and-forget Task。
- **资源所有权：**Socket、Stream、Native Handle、Subscription 必须有明确 Owner 并确定性释放。
- **错误处理：**禁止吞异常；Vendor/Infrastructure 错误在模块边界转换且保留可诊断上下文；有状态组件在需要时进入明确 Fault 状态。
- **配置：**使用 Typed Options/Configuration 并 Fail Fast；禁止在平台代码散落环境变量读取、机器路径和魔法字符串。
- **可观测性：**关键边界使用结构化日志、Health、Metrics；禁止记录密钥或敏感凭据。
- **并发：**共享可变状态必须有明确同步/所有权模型；Queue/Channel 默认必须有界，无界设计需要明确理由。
- **依赖：**新增 NuGet/Native 依赖必须说明原因并放在正确模块；Core Abstractions 禁止 Vendor SDK 依赖。
- **兼容性：**Public Contract 和 Template 视为版本化接口；Breaking Change 必须提供迁移说明、文档和测试。
- **代码形态：**优先小而内聚、职责单一的类型；禁止 God Class、Utility 垃圾桶、重复协议逻辑和复制粘贴 Provider。
- **测试：**模块自己承担行为单测；模块/Provider 边界补 Integration/Contract Test；依赖硬件的行为优先用 Simulator/Fault Injection 覆盖。

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

## 企业级基础设施质量门禁

任何将横切组件标记为“企业级基础设施”的 Issue/PR，都必须按生产运行契约评审，禁止只按“接入了哪些 NuGet/组件”验收。合并前必须按实际风险覆盖以下维度：

1. API 与模块边界：稳定契约、依赖方向、扩展接缝。
2. 可靠性：故障模式、重试/恢复、确定性关闭、局部故障语义。
3. 性能：热路径开销、分配、阻塞 I/O、有界资源使用。
4. 背压/丢失：Queue/Buffer 必须有界，明确过载行为，并暴露 Drop/Loss 信号。
5. 安全/隐私：Secret 处理、脱敏、诊断信息泄漏和最小数据暴露。
6. 配置/生命周期：Typed Configuration、Fail Fast、默认值、启动与释放。
7. 可观测语义：Metric 低基数、标准 Trace/Error 语义、Health 含义和基础设施自观测。
8. 可扩展性：第一方/第三方 Provider 走同一个 Composition Seam，禁止复制横切 Wrapper。
9. 验证：Unit + Boundary/Integration + Fault Path 测试必须证明运行不变量，不能只证明“服务已注册”。
10. 文档/兼容性：公共行为、默认值、迁移影响必须记录，并同步中英文文档。

Metrics 中，Command/Session/Connection/Request 等逐次变化 ID 默认禁止作为 Metric Attribute，除非有明确且可证明的有界基数设计；关联 ID 应进入 Log/Trace。Logging/Streaming 禁止无界缓冲，并且必须能观测过载和丢失。

## 验证权威

OpenHands 通常运行在 Linux Sandbox，提交 PR 前使用 `.openhands/setup.sh` 和 `.openhands/pre-commit.sh` 做快速验证。

UpperHost 包含 WPF/Windows Target，因此最终发布/集成权威门禁仍是 GitHub Actions Windows CI。禁止把 Linux-only 通过描述成 Windows Runtime/UI 已验证。

## 密钥

禁止提交 OpenHands 凭据、模型 API Key、PAT、签名材料、设备密钥或机器本地配置。
