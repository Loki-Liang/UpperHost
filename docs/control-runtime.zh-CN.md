# Control Runtime 控制运行时

[English](control-runtime.md) | 简体中文

`UpperHost.Control` 用于统一上位机应用侧的控制执行路径，同时避免把具体 PLC、伺服、医疗或仪器业务语义塞进平台 Core。

## Command Runtime

`CommandRuntime<TCommand,TResult>` 包装现有 `ICommandable<TCommand,TResult>`，在真正执行前依次应用 Command Guard，并返回明确状态：`Succeeded`、`Rejected`、`TimedOut`、`Cancelled`、`Faulted`。

```csharp
var runtime = new CommandRuntime<MoveCommand, MoveResult>(axis, guards);
var result = await runtime.ExecuteAsync(
    new MoveCommand(100),
    new CommandExecutionOptions(TimeSpan.FromSeconds(5)));
```

Runtime **不会**擅自认为设备 ACK 就等于机械动作完成。动作完成条件、设备状态和是否需要 Readback 仍由产品/Device Capability 定义。

本层也不负责命令排队和优先级；Command Queue / Scheduler 保留到 P2，避免一个 Runtime 同时承担过多职责。

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
