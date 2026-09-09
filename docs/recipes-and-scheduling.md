# Recipes and command scheduling

English | [简体中文](recipes-and-scheduling.zh-CN.md)

## Recipes

`RecipeDefinition` is a product-level collection of parameter values addressed by `DeviceId + ParameterKey`. `RecipeApplier` resolves each device through `IDeviceRegistry` and applies values through `IParameterProvider` plus `ParameterReadbackService`.

```csharp
var recipe = RecipeDefinition.Create(
    "product-a",
    new RecipeParameterValue("axis-x", "speed", 1200),
    new RecipeParameterValue("heater", "target", 80));

var result = await applier.ApplyAsync(recipe);
```

Readback is enabled per item by default. Recipe application is **not a distributed transaction** across physical equipment. If a later device fails, already-applied hardware values are not magically rolled back; products that require compensation must model it explicitly.

## Command scheduler

`CommandScheduler<TCommand,TResult>` owns one worker and serializes commands sent through one scheduler instance. Pending commands are ordered by `Critical`, `High`, `Normal`, then `Low`, with FIFO ordering inside the same priority.

```csharp
await using var scheduler = new CommandScheduler<AxisCommand, AxisResult>(runtime);
var result = await scheduler.EnqueueAsync(command, CommandPriority.High);
```

Priority only affects **pending** commands. The scheduler does not and cannot safely preempt a physical command already executing on hardware.

Therefore `Critical` is not an emergency-stop mechanism. Certified hardware emergency-stop and safety paths must remain independent and authoritative.
