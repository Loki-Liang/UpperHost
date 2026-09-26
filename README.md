# OpenDeviceStudio

English | [简体中文](README.zh-CN.md)

**OpenDeviceStudio is an enterprise-grade .NET upper-computer development scaffold for industrial device control, automation, and data acquisition.** It gives developers a reusable engineering baseline for industrial device-facing applications without rebuilding the same runtime, communication, testing, diagnostics, and extension infrastructure for every product.

OpenDeviceStudio treats three device-application styles as first-class peers:

1. **Control** — commands, parameters, readback, state, interlocks and diagnostics.
2. **Automation** — multi-device orchestration, workflows, state machines, alarms and recovery.
3. **Acquisition** — continuous streams, dataflow, backpressure, storage, algorithms and presentation.

Acquisition is one scaffold workload, not the center of the architecture.

The scaffold applies the same idea that makes Spring Boot productive: a small stable runtime, strong conventions, starter packages, configuration-driven auto-configuration, explicit extension points, configuration/DI/logging by default, and a runnable source application entry. Product-specific device semantics, protocol details, control/interlock policy, workflow and UI stay in `app/OpenDeviceStudio.App`; OpenDeviceStudio supplies the reusable runtime and engineering baseline.

## Start here

| Goal | Entry point |
| --- | --- |
| First time using OpenDeviceStudio | [Getting Started](docs/getting-started.md) |
| Understand the scaffold positioning | [Scaffold positioning](docs/scaffold.md) |
| Understand architecture boundaries | [Architecture](docs/architecture.md) |
| Build filters/algorithms on Acquisition data | [Signal Processing runtime](docs/signal-processing.md) |
| Understand built-in DI, configuration, logging and secondary-development seams | [Secondary-development foundation](docs/secondary-development.md) |
| Configure logging, metrics, tracing and health | [Observability](docs/observability.md) |
| Add a device, transport, protocol, workflow or plugin | [Extending OpenDeviceStudio](docs/extending.md) |
| Protect public APIs, configuration and source-scaffold compatibility | [Compatibility policy](docs/compatibility.md) |
| Use OpenHands for repository development | [OpenHands integration](docs/openhands.md) |
| AI development governance | [AGENTS.md](AGENTS.md) |
| Chinese documentation | [简体中文 README](README.zh-CN.md) |

## Built-in engineering foundation

OpenDeviceStudio is not only a device abstraction library. A product starts with a reusable application engineering baseline already wired into the scaffold:

| Foundation | Default | How product code extends it |
| --- | --- | --- |
| Hosting / lifecycle | .NET Generic Host with Start / Stop / Dispose | `AddHostedService<T>()` for long-running product services |
| Dependency injection | Microsoft.Extensions.DependencyInjection | Register product services/devices through `builder.Services`; use constructor injection |
| Configuration | Generic Host configuration + typed Options/fail-fast validation | Add product sections through `builder.Configuration` and Options Pattern |
| Logging | `Microsoft.Extensions.Logging` contract, console, optional Serilog rolling JSON file | Inject `ILogger<T>`; add/replace providers only at the composition root |
| Metrics / tracing | .NET Meter / ActivitySource, optional OpenTelemetry OTLP | Add product instrumentation/backends without leaking them into Core contracts |
| Health | `IHealthProbe` + `HealthService` | Register product/device health probes |
| Background work | Generic Host hosted services | Put discovery/heartbeat/synchronization loops in hosted services, not UI timers |
| Testing baseline | Simulator + fault-injection infrastructure | Verify protocol/control/fault paths before hardware-only validation |

The product composition root is `app/OpenDeviceStudio.App/App.xaml.cs`. Product code should use these defaults instead of creating another DI container, logging abstraction, lifecycle framework or configuration system. See [Secondary-development foundation](docs/secondary-development.md) for copyable examples and extension rules.

## Scaffold capabilities

- `IDevice + Capability` composition instead of a giant device base class.
- Automatic registration of DI-provided devices into the runtime registry.
- Multi-provider device discovery aggregation through `IDeviceDiscoverer`.
- Transport abstraction with Serial, TCP and deterministic Simulator providers.
- `IConnectionManager` owns physical connection open/close lifecycle through shared or exclusive leases so devices and UI do not compete for the same handle.
- Configuration-driven transport auto-configuration with startup fail-fast validation.
- Protocol contracts plus request/response and streaming runtimes.
- Bounded fan-out/backpressure primitives for streaming data.
- Validated/frozen block-oriented Signal Processing Stage Graph with per-partition state, lineage, gap policy, replay reuse and StreamRouter-backed bounded edges.
- A single host-owned Acquisition Session authority for Required readiness, Source start/stop, root-fault convergence, optional isolation, replay identity and terminal results.
- Generic workflow and state-machine runtimes.
- Typed in-process event bus for module decoupling.
- Alarm lifecycle service and health/transport diagnostics.
- Enterprise observability baseline: structured logging scopes, rolling JSON file logs, metrics, tracing, transport health and optional OpenTelemetry OTLP export.
- Trusted in-process module/plugin loading.
- Storage abstractions plus safe JSON file-system provider.
- WPF presentation adapter without coupling the core to WPF.
- Metadata-driven reusable WPF DeviceList, ParameterEditor, CommandPanel and AlarmPanel controls.
- Fault-injection testing helpers.
- Starter package for one-call common registration.
- Runnable `app/OpenDeviceStudio.App` WPF product scaffold.
- Windows CI that builds, tests, packs reusable NuGet modules, validates the source scaffold application, and enforces exact-base public API compatibility.

## Architecture

```text
                    OpenDeviceStudio Application
                            |
              Application / Workflow / State
                            |
                   Device + Capabilities
                            |
            +---------------+---------------+
            |                               |
   Command / Parameter                  Streaming
   Request / Response                  Dataflow
            |                               |
            +---------------+---------------+
                            |
                         Protocol
                            |
                        Transport
        Serial / TCP / USB / CAN / BLE / Vendor SDK / ...
                            |
                         Hardware
```

The scaffold/runtime owns application lifecycle, configuration, dependency injection, common communication runtime, device registration/discovery, dataflow, diagnostics and extension mechanics. A consumer product owns its device semantics, protocol, command/interlock rules, workflow and product-specific UI.

## Control path

Use request/response and typed device capabilities for PLCs, servos, temperature controllers, power supplies, instruments and actuators.

```text
UI / Workflow
  -> Command
  -> Guard / state validation
  -> Device capability
  -> Protocol
  -> Transport
  -> Hardware
  -> Ack / result / completion / readback
```

A successful byte send is not proof that a physical action has completed. Product code must define completion semantics and whether readback is required.

Software guards and interlocks do not replace hardware emergency stops, safety relays, safety PLCs or certified safety circuits.

## Automation path

Use capabilities, state machines and workflows when multiple devices cooperate in a sequence:

```text
PLC ready -> axis home -> move -> camera capture -> judge -> report result
```

Workflow steps should call device/application services instead of reaching directly into sockets or protocol buffers.

## Acquisition path

Use streaming/dataflow for telemetry, cameras, DAQ, waveforms and continuously emitted device data. The lifecycle is coordinated by one AcquisitionSession; the session coordinator is not the per-block hot path.

```text
AcquisitionSessionManager
        |
        v
AcquisitionSession
  -> Required readiness
  -> Source start/stop
  -> root-fault / optional-isolation policy
        |
Hardware -> Transport -> Decoder -> Canonical Raw -> Raw accept -> Processing / Router / UI
```

The source must not start before all Required components are Ready. Canonical Raw must be accepted by the Raw recorder ingress before processing handoff. Presentation is optional by default and must not become the lifecycle authority.

Do not force low-rate request/response devices through a high-rate streaming pipeline.

## Start secondary development in 5 minutes

OpenDeviceStudio itself is the runnable source scaffold. You do not need to build or install a project generator before starting a product. Windows and the .NET 10 SDK are required for the WPF starter application.

```powershell
git clone https://github.com/Loki-Liang/OpenDeviceStudio.git MyDeviceApp
cd MyDeviceApp
dotnet run --project app/OpenDeviceStudio.App/OpenDeviceStudio.App.csproj
```

The starter application uses Simulator by default. Continue developing the current repository as your product: add product Device capabilities, Protocol/Provider integrations, Workflows, Acquisition logic, and product UI under the application boundary while reusable runtime infrastructure remains under `src/OpenDeviceStudio.*`.

```text
MyDeviceApp/
├─ app/OpenDeviceStudio.App/          # product entry point and UI; start product work here
├─ src/OpenDeviceStudio.*/            # reusable runtime / providers / infrastructure
├─ samples/                    # reference implementations, not the product entry point
├─ tests/                      # runtime and infrastructure tests
└─ OpenDeviceStudio.slnx
```

Continue with the [zero-to-first-device guide](docs/getting-started.md).

If you are contributing to the OpenDeviceStudio runtime itself rather than building a product, then use the repository-level restore/build/test and contribution workflow.

## Minimal bootstrap

```csharp
var builder = OpenDeviceStudioApplication
    .CreateBuilder(args)
    .AddOpenDeviceStudioApplication();

await using var app = builder.Build();
await app.StartAsync();
```

Register a device in DI and OpenDeviceStudio automatically exposes it through `IDeviceRegistry` after host startup:

```csharp
builder.Services.AddSingleton<IDevice, MyDevice>();
```

A device implements only the capabilities it actually supports (`IConnectable`, `IConfigurable<T>`, `ICommandable<,>`, `IParameterProvider`, `IDataSource<T>`, etc.).

## Extension model

Add new hardware without modifying the core:

```text
OpenDeviceStudio.Transport.Usb
OpenDeviceStudio.Transport.Can
OpenDeviceStudio.Transport.Ble
OpenDeviceStudio.Transport.VendorSdk
OpenDeviceStudio.Protocol.Modbus
OpenDeviceStudio.Protocol.OpcUa
OpenDeviceStudio.Storage.Sqlite
OpenDeviceStudio.Presentation.Avalonia
Company.Device.Robot
Company.Device.Camera
Company.Transport.VendorSdk
```

Each provider is a package/module that plugs into stable contracts.

## Design rules

1. Core must remain industry-neutral.
2. Device behavior is capability composition, not inheritance depth.
3. Transport carries bytes; protocol assigns meaning.
4. Request/response and streaming are separate first-class models.
5. Control, automation and acquisition must all remain first-class application paths.
6. WPF is an adapter, not the core runtime.
7. Backpressure and loss policy must be explicit.
8. Simulator and fault injection are first-class platform features.
9. Plugins are trusted extension modules, not a sandbox.
10. Product-specific dependencies belong in starters/providers, not `OpenDeviceStudio.Abstractions`.
11. Missing or invalid platform configuration fails early and explains the exact key.
12. Software interlocks never claim to replace certified hardware safety mechanisms.

See [`docs/architecture.md`](docs/architecture.md), [`docs/extending.md`](docs/extending.md) and [`docs/getting-started.md`](docs/getting-started.md).
