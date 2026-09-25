# Getting started with UpperHost

[简体中文](getting-started.zh-CN.md) | English

This guide is for developers who have never used UpperHost. The goal is to use the scaffold to go from an empty machine to a running simulator-backed upper-computer application without first understanding the runtime internals.

## 1. What UpperHost is

UpperHost is an enterprise-grade .NET upper-computer development scaffold for industrial device control, automation, and data acquisition. It supplies a reusable runtime, a runnable source application scaffold and extension seams for three equal application styles:

- **Control**: commands, parameters, readback, state, interlocks and diagnostics.
- **Automation**: multi-device orchestration, workflows, state machines and alarms.
- **Acquisition**: continuous streams, dataflow, backpressure, storage, algorithms and presentation.

Acquisition is one capability, not the center of the architecture.

## 2. Install prerequisites

You need:

- Windows for WPF applications.
- .NET 10 SDK.
- Git.
- Visual Studio, Rider or VS Code is optional; the CLI is sufficient.

Verify:

```powershell
dotnet --info
git --version
```

## 3. Clone the scaffold and run it directly

UpperHost uses a source-first secondary-development model. The repository itself is the product engineering scaffold; you do not have to build the framework, pack a template, or generate a second project before product work starts.

```powershell
git clone https://github.com/Loki-Liang/UpperHost.git MyDeviceApp
cd MyDeviceApp
dotnet run --project app/UpperHost.App/UpperHost.App.csproj
```

The first run uses Simulator by default. When the UpperHost WPF window opens, Hosting, DI, Configuration, Observability, and the baseline transport starter are wired correctly.

Use these ownership boundaries:

```text
app/UpperHost.App/
  product startup, composition root, product UI and product-specific code

src/UpperHost.*/
  reusable runtime, providers and cross-product infrastructure

samples/
  Control / Automation / Acquisition references
```

## 4. Start product development from the application entry

Prefer product code under `app/UpperHost.App`:

```text
app/UpperHost.App/
├─ Devices/
├─ Protocols/
├─ Workflows/
├─ Acquisition/
├─ Presentation/
├─ App.xaml.cs
└─ appsettings.json
```

Move a capability into `src/UpperHost.*` only after it is genuinely reusable across products.

## 5. Learn the five concepts before adding code

### Device

A device is a domain object representing hardware. `IDevice` stays intentionally small. Add only the capability interfaces the hardware actually supports.

Examples:

```text
TemperatureController
  + IConnectable
  + ICommandable<SetTarget, Result>
  + IParameterProvider

Camera
  + IConnectable
  + ICommandable<Capture, Image>

DAQ
  + IConnectable
  + IDataSource<SampleFrame>
```

### Transport

Transport moves bytes and manages the physical/logical connection. It must not contain business commands.

Examples: serial, TCP, USB, CAN, BLE or a vendor SDK adapter.

### Protocol

Protocol turns domain commands into bytes and bytes into domain messages. Framing, checksums and message semantics live here.

### Application behavior

State machines answer **what is allowed now**. Workflows answer **what happens next**.

### Presentation

WPF is an adapter. UI code should request application/device capabilities and display state; it should not own socket loops, framing or equipment lifecycle truth.

## 6. Your first device: recommended sequence

When adding a real device, do it in this order:

1. Write a `DeviceDescriptor` and an `IDevice` implementation.
2. Add `IConnectable` if the product owns a connect/disconnect operation.
3. Model one business command as a typed record/class.
4. Implement command encoding and response decoding in Protocol.
5. Run it first against Simulator if practical.
6. Add the real transport provider only after protocol behavior is testable.
7. Register the device in DI.
8. Expose the capability to the application/UI.
9. Add state/timeout/cancellation/error handling.
10. Add hardware-specific interlock rules at the product layer, not inside Transport.

## 7. Choose the right path

### A. Device control

Use request/response and command capabilities for PLCs, servos, instruments, power supplies, temperature controllers and similar equipment.

Mental model:

```text
UI / Workflow
  -> Command
  -> Guard / State validation
  -> Device capability
  -> Protocol
  -> Transport
  -> Hardware
  -> Ack / result / readback
```

### B. Automation

Use device capabilities plus state machines and workflows when multiple devices must execute a sequence.

```text
PLC ready -> axis home -> move -> camera capture -> judge -> report result
```

### C. Acquisition

Use streaming/dataflow when hardware continuously emits telemetry, images, waveforms or sample frames.

```text
Hardware -> Transport -> Decoder -> Stream -> Dataflow -> storage / algorithm / UI
```

Do not force low-rate command devices through a streaming pipeline, and do not drive high-rate acquisition from UI timers.

## 8. Configuration

The runnable source scaffold application reads `UpperHost:Transport` from `appsettings.json`. Start with `simulator`; change to `tcp` or `serial` only when you need a physical endpoint.

Invalid required configuration should fail at startup instead of failing after the operator starts a machine cycle.

## 9. Error handling rules

For every hardware operation, decide explicitly:

- timeout;
- cancellation;
- retry policy;
- whether duplicate commands are safe;
- what state follows a failure;
- whether completion means "command accepted" or "physical action completed";
- whether readback is required.

A successful `SendAsync` call is never sufficient proof that a physical action finished.

## 10. Safety boundary

UpperHost may provide software guards, interlocks and state validation. These mechanisms do **not** replace hardware emergency stops, safety relays, safety PLCs or certified safety circuits.

## 11. Next reading

- [Secondary-development foundation](secondary-development.md)
- [Architecture](architecture.md)
- [Extending UpperHost](extending.md)
- Device Control Sample (P0 roadmap)
- AutomationStation Sample (P1 roadmap)
- DataAcquisition Sample (P2 roadmap)
