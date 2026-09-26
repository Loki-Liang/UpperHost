# Control Runtime 控制运行时

[English](control-runtime.md) | 简体中文

`UpperHost.Control` 用于统一上位机应用侧的控制执行路径，同时避免把具体 PLC、伺服、医疗或仪器业务语义塞进平台 Core。

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

软件联锁不是认证安全机制。急停、安全继电器、Safety PLC 及法规要求的硬件安全链必须独立工作，UpperHost 不能替代它们。

## Parameter Read / Write / Readback

`ParameterReadbackService` 基于现有 `IParameterProvider` 提供统一的参数读、写和读回验证。

```csharp
var parameters = new ParameterReadbackService(device);
var write = await parameters.WriteAsync("speed", 1200);
```

默认验证写入流程：

```text
查参数描述 -> 拒绝只读参数 -> Write -> 再次 Read -> Compare
```

数值比较支持容差。需要更复杂领域等价规则的产品，应继续在业务层定义，而不是污染通用平台。
