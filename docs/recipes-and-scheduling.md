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

## Command dispatching

New product code uses the host-owned `BoundedCommandDispatcher<TCommand,TResult>`, registered from the product composition root. The dispatcher keeps all queues bounded and performs priority scheduling plus resource arbitration before a device capability is invoked.

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

Priority affects pending work only and never preempts a physical command already executing. `Critical` is therefore not an emergency-stop mechanism.

`CommandScheduler<TCommand,TResult>` is retained only as the source-compatibility facade for older applications. Its implementation delegates to `BoundedCommandDispatcher`; it is not a second unbounded scheduler runtime.

Certified hardware emergency-stop and safety paths remain independent and authoritative.
