# Automation runtime

English | [简体中文](automation-runtime.zh-CN.md)

UpperHost Automation has one authoritative execution path for stations, test stands and multi-device sequences:

```text
Product UI / Application Service
        |
AutomationExecutionCoordinator
        |
compiled AutomationExecutionPlan + frozen AutomationRecipeSnapshot
        |
Step resource declaration
        |
shared #60 ICommandResourceArbiter
        |
AutomationStepContext.DispatchAsync
        |
host-owned BoundedCommandDispatcher<TCommand,TResult>
        |
CommandRuntime -> Guard / Interlock / Completion / UnknownOutcome
        |
Device capability -> Protocol -> Transport -> Hardware
```

`WorkflowRunner` remains only a compatibility/simple-workflow facade. Production Automation must not create a second station scheduler, device queue or resource coordinator.

## Execution identity and immutable input

Every run gets a unique `ExecutionId`. A compiled plan freezes the workflow id/version, node ids, resource claims, retry contract and required capabilities before side effects start. `AutomationRecipeSnapshot.Create` canonicalizes the resolved recipe JSON and stores a SHA-256 hash. The runtime never keeps the caller's mutable recipe object.

Step outputs are branch-local and typed by `AutomationDataKey<T>`. Parallel branches cannot mutate one shared dictionary; branch results are merged only after join and conflicting keys fail deterministically.

## Station and execution state authority

`AutomationExecutionCoordinator` is the station authority. One station accepts at most one active execution. It owns station transitions and rejects illegal concurrent starts.

Execution and station state are intentionally different. Execution distinguishes requests and terminal outcomes such as `PauseRequested`, `StopRequested`, `AbortRequested`, `RecoveryRequired`, `Stopped` and `Aborted`; station state remains the physical/business lifecycle (`Idle/Preparing/Running/Paused/Stopping/Faulted/Recovering/Completed`).

No device I/O, journal I/O or user callback runs while the station transition lock is held.

## Pause, Stop, Abort and emergency stop

- **Pause** is cooperative. A request is remembered and becomes `Paused` only at `AutomationCheckpointKind.SafePause` or a step policy with `PauseBoundaryAfter=true`.
- **Stop** is orderly. Once observed, no new node starts. A running step is cancelled only when its policy explicitly allows `CancelOnStop`.
- **Abort** cancels the owned execution tree immediately. Device steps must still propagate #60 command outcome semantics; cancellation after a physical side-effect boundary can become `UnknownPhysicalOutcome`.
- **Emergency stop** is not emulated by software. Hardware E-stop / safety relay / safety PLC remains outside this runtime and is only observed as product state/fault input.

## Resource ownership

A step declares its complete physical resource set with `CommandResourceClaim`. The shared #60 `ICommandResourceArbiter` canonicalizes resource keys and acquires the set in stable order.

While the lease is held, device commands must use `AutomationStepContext.DispatchAsync`. The context verifies that every command resource was declared up front and removes the already-held claims before enqueueing the command. This prevents a workflow from re-locking its own device while manual commands still contend on the same shared arbiter.

Do not call a second private dispatcher or create an Automation-only device lock.

## Parallel and join

`AutomationParallelNode` requires an explicit `MaxConcurrency` and `AutomationJoinMode`:

- `WaitAll` observes every owned child.
- `FailFast` requests cancellation after the first failing child but still observes every child before returning.

No branch is fire-and-forget. Unknown physical outcomes take precedence over ordinary sibling failure because recovery safety cannot be hidden by fail-fast cancellation.

## Retry and compensation

Automatic retry is opt-in through `AutomationRetryPolicy`. The compiler rejects retry sets containing `UnknownPhysicalOutcome`, `RecoveryRequired`, cancellation or stop. Motion/non-idempotent device operations therefore default to one attempt.

Compensation is an explicit business action, not exception rollback. If the primary step and its compensation both fail, the execution becomes `RecoveryRequired`.

## Recovery and #63 reconcile

Journal history is evidence, not physical truth. A recovery run invokes `IAutomationRecoveryReconciler` before any restart/resume decision.

`DeviceControlStateRecoveryReconciler` is the adapter for #63: the product rehydrate callback reconnects/readbacks the device and publishes readiness through `DeviceControlStateRegistry`; only `Ready` devices are reconciled.

Rules:

- `UnknownPhysicalOutcome` never auto-resumes; it requires manual intervention even if readback later succeeds.
- A known outcome may return `ResumeSafeCheckpoint` only after authoritative reconcile and only when a `SafeRecovery` checkpoint exists.
- Reconciled runs without a safe checkpoint may return `Restart`.
- Failed or incomplete reconcile remains `ManualIntervention`.

The coordinator returns a decision; it does not silently replay journaled commands.

## Execution journal

The default source scaffold registers `BoundedAutomationExecutionJournal` as a Host-owned service. It has bounded capacity, one writer, monotonic per-execution sequence numbers and explicit `FailExecution` / `DegradeAndAlert` policy.

Journal writes happen outside device resource leases. A slow file/database sink therefore cannot become the device I/O serialization point.

Products can replace `IAutomationJournalSink` at the composition root with a durable SQLite/database/file implementation.

## Composition

`AddUpperHostApplication()` installs the Automation runtime by default. For explicit composition use:

```csharp
builder.AddAutomationRuntime();
builder.AddCommandDispatcher<AxisCommand, AxisResult>();
```

The product registers `ICommandable<,>`, guards/interlocks and any product-specific `IAutomationRecoveryReconciler` before building the Host.

See `samples/UpperHost.Sample.AutomationStation` for the executable path using frozen recipe input, shared resource arbitration, bounded command dispatch, safe checkpoints, parallel join and interlock rejection.
