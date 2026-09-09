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

## Command Scheduler 命令调度

`CommandScheduler<TCommand,TResult>` 使用单 Worker，让同一个 Scheduler 中的命令串行执行。尚未执行的命令按 `Critical`、`High`、`Normal`、`Low` 排序，同优先级保持 FIFO。

```csharp
await using var scheduler = new CommandScheduler<AxisCommand, AxisResult>(runtime);
var result = await scheduler.EnqueueAsync(command, CommandPriority.High);
```

优先级只影响**尚未开始执行**的命令。Scheduler 不会、也不能安全地抢占已经在真实硬件上执行的物理动作。

因此 `Critical` 绝不是急停机制。硬件急停、安全继电器、Safety PLC 以及认证安全回路必须保持独立并拥有最终权威。
