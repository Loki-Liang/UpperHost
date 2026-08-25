# UpperHost

**UpperHost is a general-purpose .NET platform for building upper-computer / device-facing applications.** It is deliberately not tied to medical devices, acquisition systems, PLCs, cameras or any single protocol.

The project applies the same idea that makes Spring Boot productive: a small stable runtime, strong conventions, starter packages, explicit extension points, configuration/DI/logging by default, and a `dotnet new` project template.

## What is included

- `IDevice + Capability` model instead of a giant device base class.
- Transport abstraction with Serial, TCP and deterministic Simulator providers.
- Protocol contracts plus request/response and streaming runtimes.
- Bounded fan-out/backpressure primitives for streaming data.
- Generic workflow and state-machine runtimes.
- Health and transport metrics primitives.
- Trusted in-process module/plugin loading.
- WPF presentation adapter without coupling the core to WPF.
- Fault-injection testing helpers.
- Starter package for one-call common registration.
- `dotnet new upperhost` WPF template.
- Windows CI that builds, tests, packs NuGet packages and smoke-builds a generated template application.

## Architecture

```text
                 UpperHost Application
                         |
      +------------------+------------------+
      |                  |                  |
 Presentation       Workflow/State       Diagnostics
      |                  |                  |
      +------------- Device + Capabilities-+
                         |
              +----------+----------+
              |                     |
       Request/Response          Streaming
              |                     |
              +--------- Protocol --+
                         |
                     Transport
          Serial / TCP / Simulator / ...
```

The framework owns application lifecycle, configuration, dependency injection, common communication runtime, diagnostics and extension mechanics. A product owns its device semantics, protocol, workflow and product-specific UI.

## Build

Requires .NET 10 SDK. WPF projects require Windows.

```powershell
dotnet restore UpperHost.slnx
dotnet build UpperHost.slnx -c Release
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release
```

.NET 10 uses the XML `.slnx` solution format by default, which keeps the repository solution file small and maintainable.

## Create a project

Pack and install the template locally:

```powershell
dotnet pack templates/UpperHost.Templates.csproj -c Release -o artifacts/packages
dotnet new install artifacts/packages/UpperHost.Templates.0.1.0-alpha.1.nupkg

dotnet new upperhost -n MyDeviceApp --transport simulator
```

Switch the starter transport when creating a project:

```powershell
dotnet new upperhost -n PlcStation --transport tcp
dotnet new upperhost -n InstrumentConsole --transport serial
```

## Minimal bootstrap

```csharp
var builder = UpperHostApplication
    .CreateBuilder(args)
    .AddUpperHostDefaults()
    .AddSimulatorTransport();

await using var app = builder.Build();
await app.StartAsync();
```

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

See [`docs/architecture.md`](docs/architecture.md) and [`docs/extending.md`](docs/extending.md).

## Status

`0.1.0-alpha.1` establishes the stable architectural seams and end-to-end packaging/template pipeline. Additional protocol, transport, storage and UI-control providers can now be added without changing the core model.
