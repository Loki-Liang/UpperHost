# UpperHost secondary-development foundation

English | [简体中文](secondary-development.zh-CN.md)

This guide answers two questions:

1. **Which engineering and upper-computer capabilities does UpperHost already provide so a product does not rebuild them?**
2. **Where should a secondary-development project use, replace, or extend those capabilities?**

UpperHost is a source-first enterprise .NET upper-computer development scaffold. Product code belongs in `app/UpperHost.App` first; only capabilities proven reusable across products should move into `src/UpperHost.*`.

## 1. Built-in capability map

| Capability | Current UpperHost baseline | Secondary-development entry |
| --- | --- | --- |
| Host / lifecycle | .NET Generic Host with unified Start / Stop / Dispose | `UpperHostApplication.CreateBuilder()`, `AddHostedService<T>()` |
| Dependency injection | Microsoft.Extensions.DependencyInjection | `builder.Services` |
| Configuration | Generic Host configuration including appsettings, environment and command-line sources | `builder.Configuration`, Options Pattern |
| Logging | `Microsoft.Extensions.Logging` contract; console; optional Serilog rolling JSON files | Inject `ILogger<T>`; configure Observability or standard logging providers |
| Metrics / tracing | .NET `Meter` / `ActivitySource`; optional OpenTelemetry OTLP | Standard OpenTelemetry extension points |
| Health | `IHealthProbe` + `HealthService`; built-in transport/log-buffer probes | Implement and register `IHealthProbe` |
| Device model | `IDevice` + capability composition; automatic `IDeviceRegistry` registration | Implement `IDevice`, `IConnectable`, `ICommandable<,>`, etc. |
| Device discovery | Aggregated `IDeviceDiscoverer` providers | Implement/register `IDeviceDiscoverer` |
| Connection lifecycle | `IConnectionManager` with shared/exclusive leases | Use managed connections instead of competing physical handles |
| Transport | Serial, TCP and Simulator with common `ITransport` seam | Implement `ITransport`, register through `AddUpperHostTransport<T>()` |
| Protocol | Command encoders, message decoders, request/response and streaming boundaries | Implement `ICommandEncoder<T>` / `IMessageDecoder<T>` |
| Control runtime | Host-owned bounded command dispatcher, guards/interlocks, UnknownOutcome, resource arbitration, parameter/readback foundations | Register each typed command contract with `AddCommandDispatcher<TCommand,TResult>()`; define product safety/completion semantics |
| Streaming / dataflow | `StreamRouter<T>` with sealed topology, per-branch bounded QoS, Required/Optional failure semantics, ownership and metrics; legacy `FanOutHub<T>` remains compatibility-only | Register the full processing topology before `StartAsync()` and choose capacity/overflow/failure policy per branch |
| Workflow | `WorkflowRunner` | Define product `WorkflowDefinition` / `IWorkflowStep` |
| State machine | Generic `StateMachine<TState,TTrigger>` | Define product states and triggers |
| Events | Typed `IEventBus` | Publish/subscribe product events instead of static globals |
| Alarms | `IAlarmService` raise/acknowledge/clear lifecycle | Define product alarm rules |
| Storage | `IKeyValueStore` + JSON file-system provider | `AddFileSystemStorage()` or implement another provider |
| Modules / plugins | `IUpperHostModule` and trusted in-process loading | Register services in module `ConfigureServices` |
| Presentation | WPF adapter and reusable device/parameter/command/alarm controls | Keep product UI in `app/UpperHost.App` |
| Testing | Simulator and fault-injection infrastructure | Cover simulator and fault paths before hardware-only validation |

## 2. Start from one composition root

The canonical product entry is `app/UpperHost.App/App.xaml.cs`. Install platform defaults first, then register product services:

```csharp
var builder = UpperHostApplication
    .CreateBuilder(e.Args)
    .AddUpperHostApplication();

builder.Services.AddSingleton<MainWindow>();
builder.Services.AddSingleton<IMyMachineService, MyMachineService>();
builder.Services.AddSingleton<IDevice, MyDevice>();

_host = builder.Build();
await _host.StartAsync();
```

Keep platform infrastructure in UpperHost and product composition in the application root. Avoid service locators and mutable global singletons.

## 3. Dependency injection

UpperHost uses standard Microsoft DI:

```csharp
builder.Services.AddSingleton<IMachineRuntime, MachineRuntime>();
builder.Services.AddTransient<ICommandFactory, CommandFactory>();
builder.Services.AddSingleton<IDevice, TemperatureController>();
```

Use constructor injection:

```csharp
public sealed class MachineRuntime(
    IDeviceRegistry devices,
    ILogger<MachineRuntime> logger)
{
}
```

Typical lifetime guidance:

- Singleton for device runtimes, registries, long-lived connection managers and application coordinators.
- Transient for stateless low-cost objects.
- Scoped only when the desktop product creates explicit scopes; WPF has no automatic request scope.

Replace a platform implementation at the composition root while preserving the public contract instead of modifying Core for one product.

## 4. Configuration and options

`UpperHostApplication.CreateBuilder()` is based on Generic Host. UpperHost baseline sections are:

```text
UpperHost:Transport
UpperHost:Observability
```

Create product-owned sections for product settings and bind them through the standard Options Pattern:

```csharp
builder.Services
    .AddOptions<MyMachineOptions>()
    .Bind(builder.Configuration.GetSection("MyMachine"))
    .Validate(
        options => options.HomeTimeoutSeconds > 0,
        "MyMachine:HomeTimeoutSeconds must be greater than 0.")
    .ValidateOnStart();
```

Connection, address, timeout and other mandatory settings should fail fast during startup whenever possible.

## 5. Logging

Product code should depend on `ILogger<T>`, not Serilog APIs:

```csharp
public sealed class AxisService(ILogger<AxisService> logger)
{
    public Task MoveAsync(string axisId, double position)
    {
        logger.LogInformation(
            "Axis {AxisId} moving to {Position}",
            axisId,
            position);

        return Task.CompletedTask;
    }
}
```

The current pipeline is:

```text
Product code
  -> Microsoft.Extensions.Logging
      -> Console
      -> optional Serilog rolling JSON file
```

File output is configured under `UpperHost:Observability:Logging:File`. A product may disable it and add another standard logging provider at the composition root while keeping business code on `ILogger<T>`.

Never log passwords, tokens, device secrets, private keys or raw credentials.

## 6. Metrics, tracing and health

UpperHost provides a common observability baseline:

- Meter name: `UpperHost`.
- ActivitySource name: `UpperHost`.
- Optional OpenTelemetry OTLP export.
- `IHealthProbe` / `HealthService`.
- Common observation points for transports, commands, reconnects, alarms and streams.

Register product health through `IHealthProbe`:

```csharp
public sealed class RobotHealthProbe : IHealthProbe
{
    public string Name => "robot";

    public Task<HealthReport> CheckAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new HealthReport(
            Name,
            HealthStatus.Healthy,
            "Robot is ready."));
}

builder.Services.AddSingleton<IHealthProbe, RobotHealthProbe>();
```

Keep metric attributes low-cardinality; per-execution identifiers belong in logs/traces rather than metric labels.

## 7. Hosted services

Long-running discovery, heartbeat and synchronization loops should use the Generic Host:

```csharp
builder.Services.AddHostedService<DeviceHeartbeatService>();
```

Hosted services must honor cancellation, stop deterministically and avoid unmanaged fire-and-forget work.

## 8. Device extension

Implement a small `IDevice` and add only the capabilities the hardware supports:

```csharp
public sealed class TemperatureController : IDevice
{
    public DeviceDescriptor Descriptor { get; } =
        new("temp-01", "Temperature Controller");

    public DeviceState State { get; private set; } = DeviceState.Offline;

    public IReadOnlyCollection<string> Capabilities { get; } =
        ["connect", "command", "parameter"];
}
```

Then compose the hardware-specific capabilities:

```text
TemperatureController
  + IConnectable
  + ICommandable<SetTarget, SetTargetResult>
  + IParameterProvider

DAQ
  + IConnectable
  + IDataSource<SampleFrame>
```

Register devices through DI:

```csharp
builder.Services.AddSingleton<IDevice, TemperatureController>();
```

They are added to `IDeviceRegistry` after host startup.

## 9. Transport and protocol extension

For USB, CAN, BLE or vendor SDK connectivity, implement the appropriate transport/provider boundary and register through the common starter seam so connection ownership, resilience and observability remain consistent.

Protocol owns domain-command encoding, framing, decoding and recovery. Implement `ICommandEncoder<TCommand>` and/or `IMessageDecoder<TMessage>`. Transport should not contain product commands, UI updates or product state machines.

## 10. Control and automation

Reuse existing runtime pieces instead of rebuilding them in button handlers:

- `BoundedCommandDispatcher<TCommand,TResult>` registered by the composition root
- `CommandRuntime<TCommand,TResult>` as the execution/guard/completion layer under the dispatcher
- guards/interlocks and explicit command safety metadata
- `IParameterProvider` plus readback (coordinated through #63 resource arbitration)
- recipe integration through the same dispatcher
- `WorkflowRunner`
- `StateMachine<TState,TTrigger>`
- `IEventBus`
- `IAlarmService`

A normal product path is:

```text
WPF / Workflow
  -> Application Service
  -> Guard / State / Interlock
  -> Device Capability
  -> Protocol
  -> Transport
  -> Hardware
  -> Result / Completion / Readback
```

A successful byte write is not proof that a physical action completed. Product code owns completion semantics.

## 11. Acquisition

AddUpperHostApplication() already registers the host-owned AcquisitionSessionManager. A product creates one frozen AcquisitionSessionDefinition per live/replay run and supplies its Source plus Required/Optional route adapters; it does not create another lifecycle coordinator.

```csharp
var manager = services.GetRequiredService<AcquisitionSessionManager>();
await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
    AcquisitionSessionMode.LiveAcquisition,
    sources,
    requiredComponents,
    optionalComponents));

await session.StartAsync(startRequestToken);
var result = await session.StopAsync();
```

For high-rate data, keep the Session coordinator out of the per-block hot path:

```text
Hardware
 -> Transport / Provider
 -> Decoder / Canonical Raw
 -> Raw recorder accepted
 -> Processing
 -> bounded Router
 -> Algorithm / Processed Storage / UI
```

Required readiness is complete before Source start. Required faults terminate/converge through the Session; Optional presentation/algorithm faults are isolated unless the frozen definition explicitly escalates them. Replay sources are read-only by default and reuse processing implementations under a new ProcessingEpoch.

Post-processing fan-out uses one production Router contract instead of creating a queue per feature:

```csharp
var router = new StreamRouter<ProcessedBlock>(ownership);
router.RegisterBranch(
    StreamBranchOptions.Required(
        "algorithm-a", "Algorithm A", capacity: 256, overflow: StreamOverflowPolicy.Wait),
    ConsumeAlgorithmAsync);
router.RegisterBranch(
    StreamBranchOptions.Optional(
        "presentation", "Waveform UI", capacity: 8, overflow: StreamOverflowPolicy.DropOldest),
    RenderAsync);

await router.StartAsync(cancellationToken);

var publish = await router.PublishAsync(block, cancellationToken);
if (publish.RequiresStop)
    throw new InvalidOperationException("A required stream route failed.");
```

Every branch is bounded. Required branches reject lossy overflow policies; Optional branches reject `Wait` so a slow UI cannot backpressure Required processing. The router preserves its serialized publish sequence per branch, owns consumer tasks and fault observation, exposes branch queue/drop/lag counters, and uses `IStreamItemOwnership<T>` when pooled/shared blocks need retain/release lifetime management. The canonical Raw recorder remains an ingress durability path (#58), not an ordinary Optional Router branch.

Define capacity, backpressure, loss policy and UI downsampling explicitly. Do not use a WPF UI timer as the acquisition clock.

## 12. Storage

Simple products can use:

```csharp
builder.AddFileSystemStorage("data");
```

Reusable database backends should remain independent storage providers behind stable abstractions instead of leaking database SDKs into `UpperHost.Abstractions`.

## 13. Modules and plugins

Trusted in-process modules implement `IUpperHostModule` and register dependencies from `ConfigureServices`. This is an extension mechanism, not a security sandbox.

## 14. Testing

Before hardware-only validation, prefer:

- `SimulatorTransport`;
- device simulators;
- `FaultInjectingTransport`;
- unit tests;
- provider/protocol contract tests;
- timeout/cancellation/reconnect/fault tests.

## 15. Do not rebuild these per product

A normal product should not recreate its own DI container, logging abstraction, host lifecycle, configuration framework, connection manager, Serial/TCP baseline, generic command/workflow/event/alarm runtime, observability foundation or simulator/fault-injection infrastructure.

Product-owned work includes device semantics, private protocols, commands/parameters, completion/readback rules, interlocks, process workflows, UI, algorithms and product persistence policies.

## 16. Recommended product layout

```text
app/UpperHost.App/
├─ Devices/
├─ Protocols/
├─ Services/
├─ Control/
├─ Workflows/
├─ Acquisition/
├─ Presentation/
├─ Options/
├─ App.xaml.cs
└─ appsettings.json

src/UpperHost.*/
└─ only cross-product reusable runtime/provider/infrastructure
```

## 17. Continue reading

- [Getting Started](getting-started.md)
- [Architecture](architecture.md)
- [Extending UpperHost](extending.md)
- [Control Runtime](control-runtime.md)
- [Recipes and Scheduling](recipes-and-scheduling.md)
- [Observability](observability.md)
- [Provider design](providers.md)
