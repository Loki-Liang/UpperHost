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

## Authoritative device state and polling

`DeviceSnapshotStore<TState>` is the single immutable read model for poll, push/event, command completion, parameter readback and rehydrate observations. Observations carry a connection epoch and monotonic observed timestamp; stale-epoch or older observations cannot overwrite newer state. Freshness is computed with `TimeProvider`, so state can become `Stale` without waiting for another device I/O.

`DevicePollRuntime<TState>` owns one bounded scheduling calendar and one bounded work channel. Each PollGroup has at most one queued/running invocation plus an optional coalesced follow-up; missed ticks are Skip/Coalesce, never an unbounded backlog.

Polling and parameter reads default to **Exclusive** resource arbitration. Use `SharedRead` only when a Provider/Protocol capability explicitly guarantees concurrent reads.

## Typed parameter read/write/readback

New production code uses `TypedParameterRuntime<T>`, not direct UI calls to `IParameterProvider`.

```csharp
var contract = new ParameterContract<double>(
    "speed",
    ReadbackComparer: ParameterComparers.Absolute(0.1),
    Range: new ParameterRange<double>(0, 3000));

var result = await parameters.WriteAsync(contract, 1200);
```

The runtime validates range/domain before I/O, acquires the same #60 resource arbiter used by commands/polling, performs write + readback in one ownership scope, commits verified observed state to `DeviceSnapshotStore<T>`, and reports `UnknownOutcome` when a write may have happened but readback cannot establish final device state.

`ParameterReadbackService` remains only as a compatibility/simple facade. It is not the authoritative enterprise path for new Source-Scaffold products.

## Reconnect and rehydrate

`DeviceControlStateRegistry` owns the connection epoch and required rehydrate barrier. Reconnect/disconnect advances the epoch, invalidates old snapshots, rejects late old-epoch poll/event results and keeps the device non-Ready until all required state/parameter reads complete.

Control metrics are emitted through the existing `UpperHost` Meter with low-cardinality operation/result/quality tags. Device IDs, serial numbers, parameter keys and execution IDs stay in logs/traces rather than default metric tags.
