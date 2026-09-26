# Control runtime

English | [简体中文](control-runtime.zh-CN.md)

`UpperHost.Control` standardizes the application-side control path without moving product semantics into the platform.

## Command runtime

`CommandRuntime<TCommand,TResult>` wraps an existing command target and applies command guards before execution. Results distinguish `Succeeded`, `Rejected`, `TimedOut`, `Cancelled`, `Faulted` and `UnknownOutcome`. `UnknownOutcome` means the command crossed the side-effect boundary but the final physical device state is unknown; callers must reconcile/read back before retrying.

```csharp
var runtime = new CommandRuntime<MoveCommand, MoveResult>(axis, guards);
var result = await runtime.ExecuteAsync(
    new MoveCommand(100),
    new CommandExecutionOptions(TimeSpan.FromSeconds(5)));
```

The runtime does **not** decide whether a device ACK means physical completion. The product/device capability owns completion semantics and readback rules.

Production queueing and arbitration are owned by `BoundedCommandDispatcher<TCommand,TResult>`. It provides bounded admission, weighted priority fairness, per-resource pending limits, deadlock-safe resource sets, queue deadlines, connection-epoch rejection and host-owned lifecycle.

```csharp
builder.AddCommandDispatcher<MoveCommand, MoveResult>(
    new BoundedCommandDispatcherOptions(
        Capacity: 256,
        PerResourcePendingCapacity: 64,
        MaxConcurrency: 4));
```

Use `CommandResourceAccess.SharedRead` only when the Provider/Protocol explicitly supports concurrent reads. Mutating/motion commands use `Exclusive`. `QueueTimeout` covers admission and resource wait before device dispatch; execution timeout remains a separate `CommandExecutionOptions.Timeout`.

The legacy `CommandScheduler<TCommand,TResult>` remains as a compatibility facade and delegates to the bounded dispatcher. New code should not create a second scheduler path.

## Command guards

Implement `ICommandGuard<TCommand>` for application preconditions that can reject a command before it reaches the device.

Guards are deterministic application policy seams: state validation, authorization-like product rules, maintenance mode and similar concerns can be composed here.

## Software interlocks

`IInterlock<TCommand>` expresses software interlock checks. `InterlockCommandGuard<TCommand>` adapts a set of interlocks into the command runtime.

```text
Command -> Guard -> Interlock(s) -> Device capability
```

Software interlocks are not certified safety mechanisms. Emergency stops, safety relays, safety PLCs and other required hardware safety chains remain authoritative and must operate independently of UpperHost.

## Parameter read/write/readback

`ParameterReadbackService` standardizes parameter reads and verified writes over the existing `IParameterProvider` capability.

```csharp
var parameters = new ParameterReadbackService(device);
var write = await parameters.WriteAsync("speed", 1200);
if (!write.Verified) { /* product policy */ }
```

A verified write performs:

```text
Find descriptor -> reject read-only -> write -> read again -> compare
```

Numeric readback comparison supports a configurable tolerance. Products that require domain-specific equivalence should keep that policy above the generic service.
