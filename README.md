# UpperHost

English | [简体中文](README.zh-CN.md)

**UpperHost is a general-purpose .NET platform for building upper-computer / device-facing applications.** It is deliberately not tied to medical devices, acquisition systems, PLCs, cameras, robots, laboratory instruments or any single protocol.

UpperHost treats three device-application styles as first-class peers:

1. **Control** — commands, parameters, readback, state, interlocks and diagnostics.
2. **Automation** — multi-device orchestration, workflows, state machines, alarms and recovery.
3. **Acquisition** — continuous streams, dataflow, backpressure, storage, algorithms and presentation.

Acquisition is one platform workload, not the center of the architecture.

The project applies the same idea that makes Spring Boot productive: a small stable runtime, strong conventions, starter packages, configuration-driven auto-configuration, explicit extension points, configuration/DI/logging by default, and a `dotnet new` project template.

## Start here

| Goal | Entry point |
| --- | --- |
| First time using UpperHost | [Getting Started](docs/getting-started.md) |
| Understand platform boundaries | [Architecture](docs/architecture.md) |
| Add a device, transport, protocol, workflow or plugin | [Extending UpperHost](docs/extending.md) |
| Chinese documentation | [简体中文 README](README.zh-CN.md) |

## Open-box platform capabilities

- `IDevice + Capability` composition instead of a giant device base class.
- Automatic registration of DI-provided devices into the runtime registry.
- Multi-provider device discovery aggregation through `IDeviceDiscoverer`.
- Transport abstraction with Serial, TCP and deterministic Simulator providers.
- Configuration-driven transport auto-configuration with startup fail-fast validation.
- Protocol contracts plus request/response and streaming runtimes.
- Bounded fan-out/backpressure primitives for streaming data.
- Generic workflow and state-machine runtimes.
- Typed in-process event bus for module decoupling.
- Alarm lifecycle service and health/transport diagnostics.
- Trusted in-process module/plugin loading.
- Storage abstractions plus safe JSON file-system provider.
- WPF presentation adapter without coupling the core to WPF.
- Metadata-driven reusable WPF DeviceList, ParameterEditor, CommandPanel and AlarmPanel controls.
- Fault-injection testing helpers.
- Starter package for one-call common registration.
- `dotnet new upperhost` WPF template.
- Windows CI that builds, tests, packs NuGet packages and smoke-builds a generated template application outside the source repository.

## Architecture

```text
                    UpperHost Application
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

The framework owns application lifecycle, configuration, dependency injection, common communication runtime, device registration/discovery, dataflow, diagnostics and extension mechanics. A product owns its device semantics, protocol, command/interlock rules, workflow and product-specific UI.

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

Use streaming/dataflow for telemetry, cameras, DAQ, waveforms and continuously emitted device data:

```text
Hardware -> Transport -> Decoder -> Stream -> Dataflow -> Storage / Algorithm / UI
```

Do not force low-rate request/response devices through a high-rate streaming pipeline.

## Build

Requires .NET 10 SDK. WPF projects require Windows.

```powershell
dotnet restore UpperHost.slnx
dotnet build UpperHost.slnx -c Release
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release
```

## Create a project

Pack and install the template locally:

```powershell
dotnet pack templates/UpperHost.Templates.csproj -c Release -o artifacts/packages
dotnet new install artifacts/packages/UpperHost.Templates.0.1.0-alpha.1.nupkg

dotnet new upperhost -n MyDeviceApp --transport simulator
```

Other baseline choices:

```powershell
dotnet new upperhost -n PlcStation --transport tcp
dotnet new upperhost -n InstrumentConsole --transport serial
```

The generated app reads `UpperHost:Transport` from `appsettings.json`; there is no transport wiring boilerplate in `App.xaml.cs`.

Continue with the [zero-to-first-device guide](docs/getting-started.md).

## Minimal bootstrap

```csharp
var builder = UpperHostApplication
    .CreateBuilder(args)
    .AddUpperHostApplication();

await using var app = builder.Build();
await app.StartAsync();
```

Register a device in DI and UpperHost automatically exposes it through `IDeviceRegistry` after host startup:

```csharp
builder.Services.AddSingleton<IDevice, MyDevice>();
```

A device implements only the capabilities it actually supports (`IConnectable`, `IConfigurable<T>`, `ICommandable<,>`, `IParameterProvider`, `IDataSource<T>`, etc.).

## Extension model

Add new hardware without modifying the core:

```text
UpperHost.Transport.Usb
UpperHost.Transport.Can
UpperHost.Transport.Ble
UpperHost.Protocol.Modbus
UpperHost.Protocol.OpcUa
UpperHost.Storage.Sqlite
UpperHost.Presentation.Avalonia
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
10. Product-specific dependencies belong in starters/providers, not `UpperHost.Abstractions`.
11. Missing or invalid platform configuration fails early and explains the exact key.
12. Software interlocks never claim to replace certified hardware safety mechanisms.

See [`docs/architecture.md`](docs/architecture.md), [`docs/extending.md`](docs/extending.md) and [`docs/getting-started.md`](docs/getting-started.md).
