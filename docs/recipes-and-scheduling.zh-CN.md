# Recipe 与 Command Scheduler

[English](recipes-and-scheduling.md) | 简体中文

## Recipe 配方

`RecipeDefinition` 用 `DeviceId + ParameterKey` 表达一组产品参数。`RecipeApplier` 通过 `IDeviceRegistry` 找到设备，再使用 `IParameterProvider` 和 `ParameterReadbackService` 写入并验证参数。

```csharp
var recipe = RecipeDefinition.Create(
    "product-a",
    new RecipeParameterValue("axis-x", "speed", 1200),
    new RecipeParameterValue("heater", "target", 80));

var result = await applier.ApplyAsync(recipe);
```

每项默认开启 Readback。需要明确：配方应用**不是跨真实硬件的分布式事务**。如果后续设备写入失败，已经写入前面设备的值不会被平台“魔法回滚”；需要补偿动作的产品必须显式建模。

## Command Dispatching 命令调度

新产品代码统一使用由 Composition Root 注册、受 Host 生命周期管理的 `BoundedCommandDispatcher<TCommand,TResult>`。所有队列有界，真正调用 Device Capability 前统一完成优先级调度和资源仲裁。

```csharp
builder.AddCommandDispatcher<AxisCommand, AxisResult>();

var result = await dispatcher.EnqueueAsync(
    command,
    new CommandDispatchOptions(
        Priority: CommandPriority.High,
        Resources: [new CommandResourceKey("axis", "x")],
        Safety: CommandSafetyMetadata.MutatingNonIdempotent,
        QueueTimeout: TimeSpan.FromSeconds(2)));
```

优先级只影响尚未开始的命令，绝不会抢占已经在真实硬件上执行的物理动作，因此 `Critical` 不是急停机制。

旧 `CommandScheduler<TCommand,TResult>` 仅作为源码兼容 facade 保留，其内部已经委托 `BoundedCommandDispatcher`，不再存在第二套无界调度 Runtime。

硬件急停、安全继电器、Safety PLC 和认证安全回路继续独立并拥有最终权威。
