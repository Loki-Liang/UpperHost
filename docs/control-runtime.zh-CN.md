# Control Runtime 控制运行时

[English](control-runtime.md) | 简体中文

`OpenDeviceStudio.Control` 用于统一上位机应用侧的控制执行路径，同时避免把具体 PLC、伺服、医疗或仪器业务语义塞进平台 Core。

## Command Runtime

`CommandRuntime<TCommand,TResult>` 包装命令目标并执行 Command Guard。结果明确区分 `Succeeded`、`Rejected`、`TimedOut`、`Cancelled`、`Faulted` 与 `UnknownOutcome`。`UnknownOutcome` 表示命令已经越过副作用边界，但物理设备最终状态未知；上层必须 Readback/Reconcile 后才能决定是否重试。

```csharp
var runtime = new CommandRuntime<MoveCommand, MoveResult>(axis, guards);
var result = await runtime.ExecuteAsync(
    new MoveCommand(100),
    new CommandExecutionOptions(TimeSpan.FromSeconds(5)));
```

Runtime **不会**擅自认为设备 ACK 就等于机械动作完成。动作完成条件、设备状态和是否需要 Readback 仍由产品/Device Capability 定义。

生产级排队与仲裁由 `BoundedCommandDispatcher<TCommand,TResult>` 统一负责：有界 Admission、带公平性的优先级、每资源 Pending 上限、ResourceSet 防死锁仲裁、Queue Deadline、Connection Epoch 拒绝以及 Host 生命周期。

```csharp
builder.AddCommandDispatcher<MoveCommand, MoveResult>(
    new BoundedCommandDispatcherOptions(
        Capacity: 256,
        PerResourcePendingCapacity: 64,
        MaxConcurrency: 4));
```

只有 Provider/Protocol 明确支持并发读时才使用 `CommandResourceAccess.SharedRead`；写入、运动和其他修改操作使用 `Exclusive`。 `QueueTimeout` 只覆盖进入设备 Dispatch 前的 Admission/Resource Wait；真正设备执行超时仍由 `CommandExecutionOptions.Timeout` 独立控制。

旧 `CommandScheduler<TCommand,TResult>` 仅保留兼容 facade，内部已经委托 bounded dispatcher。新代码禁止再建立第二套 Scheduler。

## Command Guard

实现 `ICommandGuard<TCommand>`，可以在命令到达设备前执行应用级前置检查。

适合：设备状态约束、维护模式、产品权限规则、运行条件等。多个 Guard 可以组合，不需要把大量 `if` 散落在按钮事件中。

## 软件 Interlock

`IInterlock<TCommand>` 表达软件联锁；`InterlockCommandGuard<TCommand>` 把多个 Interlock 适配进 Command Runtime：

```text
Command -> Guard -> Interlock(s) -> Device Capability
```

软件联锁不是认证安全机制。急停、安全继电器、Safety PLC 及法规要求的硬件安全链必须独立工作，OpenDeviceStudio 不能替代它们。

## 权威 Device State 与 Polling

`DeviceSnapshotStore<TState>` 是 Poll、Push/Event、Command Completion、Parameter Readback、Rehydrate 共用的唯一 immutable read model。Observation 带 ConnectionEpoch 与单调 ObservedTimestamp；旧 Epoch 和更旧 Observation 不能覆盖新状态。Freshness 通过 `TimeProvider` 查询时派生，因此没有新 I/O 时也能自然进入 `Stale`。

`DevicePollRuntime<TState>` 只维护一套有界 Schedule Calendar 和一条有界 Work Channel。每个 PollGroup 最多一个 queued/running 调用以及一个可选 coalesced follow-up；错过周期使用 Skip/Coalesce，禁止补排成无界 backlog。

Polling 和 Parameter Read 默认使用 **Exclusive** 资源仲裁。只有 Provider/Protocol Capability 明确保证并发读安全时，产品才可以显式 opt-in `SharedRead`。

## Typed Parameter Read / Write / Readback

新产品权威路径使用 `TypedParameterRuntime<T>`，UI 不再直接调用 `IParameterProvider`。

```csharp
var contract = new ParameterContract<double>(
    "speed",
    ReadbackComparer: ParameterComparers.Absolute(0.1),
    Range: new ParameterRange<double>(0, 3000));

var result = await parameters.WriteAsync(contract, 1200);
```

Runtime 在 I/O 前完成 Range/Domain 校验，复用 #60 Command/Polling 同一个 Resource Arbiter，在同一 ownership scope 内完成 Write + Readback，把真实 observed value 提交到 `DeviceSnapshotStore<T>`；如果写入可能已经发生但 Readback 无法确认最终状态，则返回 `UnknownOutcome`，不会把 Requested Value 冒充 Observed State。

旧 `ParameterReadbackService` 只作为兼容/简单 facade 保留，不再是新 Source-Scaffold 产品的企业级权威路径。

## Reconnect / Rehydrate

`DeviceControlStateRegistry` 统一管理 ConnectionEpoch 和 Required Rehydrate Barrier。Reconnect/Disconnect 会推进 Epoch、使旧 Snapshot 失效、拒绝旧 Epoch 晚到 Poll/Event；所有 Required State/Parameter Read 成功前设备都不能进入 Ready。

Control Metrics 继续复用现有 `OpenDeviceStudio` Meter，只使用低基数 operation/result/quality 标签。DeviceId、序列号、ParameterKey、ExecutionId 默认只进入 Log/Trace，不进入 Meter tag。
