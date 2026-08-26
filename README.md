# UpperHost

**UpperHost is a general-purpose .NET platform for building upper-computer / device-facing applications.** It is deliberately not tied to medical devices, acquisition systems, PLCs, cameras, robots, laboratory instruments or any single protocol.

The project applies the same idea that makes Spring Boot productive: a small stable runtime, strong conventions, starter packages, configuration-driven auto-configuration, explicit extension points, configuration/DI/logging by default, and a `dotnet new` project template.

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
       +--------------------+--------------------+
       |                    |                    |
  Presentation         Workflow/State      Events/Diagnostics
       |                    |                    |
       +-------------- Device + Capabilities ---+
                            |
                  Discovery / Registry
                            |
                 +----------+----------+
                 |                     |
          Request/Response          Streaming
                 |                     |
                 +--------- Protocol --+
                            |
                        Transport
             Serial / TCP / Simulator / ...
                            |
                         Hardware
```

The framework owns application lifecycle, configuration, dependency injection, common communication runtime, device registration/discovery, dataflow, diagnostics and extension mechanics. A product owns its device semantics, protocol, workflow and product-specific UI.

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
```

Each provider is a package/module that plugs into stable contracts.

## Design rules

1. Core must remain industry-neutral.
2. Device behavior is capability composition, not inheritance depth.
3. Transport carries bytes; protocol assigns meaning.
4. Request/response and streaming are separate first-class models.
5. WPF is an adapter, not the core runtime.
6. Backpressure and loss policy must be explicit.
7. Simulator and fault injection are first-class platform features.
8. Plugins are trusted extension modules, not a sandbox.
9. Product-specific dependencies belong in starters/providers, not `UpperHost.Abstractions`.
10. Missing or invalid platform configuration fails early and explains the exact key.

See [`docs/architecture.md`](docs/architecture.md) and [`docs/extending.md`](docs/extending.md).
