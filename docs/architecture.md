# UpperHost architecture

UpperHost is a **general device-application platform**, not a framework for one industry or one hardware category.

## Stable platform boundary

```text
Presentation (WPF / WinUI / Avalonia / CLI / Service)
        |
Application / Workflow / State Machine / Event Bus
        |
Device Registry + Discovery + Capability Composition
        |
Protocol (request-response or streaming)
        |
Transport (Serial / TCP / USB / CAN / BLE / ...)
```

Cross-cutting platform capabilities are Configuration, Hosting/DI, Dataflow, Persistence providers, Alarms, Diagnostics, Plugins and Testing.

## Device model

`IDevice` is intentionally small. Features are expressed through capability interfaces such as `IConnectable`, `IConfigurable<T>`, `ICommandable<TCommand,TResult>`, `ICalibratable<,>`, `IParameterProvider` and `IDataSource<T>`.

This avoids forcing a PLC, camera, motor controller, laboratory instrument and biosignal acquisition unit into one inheritance tree.

Any `IDevice` registered in DI is automatically added to `IDeviceRegistry` when the Generic Host starts. `IDeviceDiscoveryService` aggregates any installed `IDeviceDiscoverer` providers without requiring the core to know vendor discovery protocols.

## Auto-configuration

`AddUpperHostApplication()` installs the platform defaults and selects a baseline transport from `UpperHost:Transport:Type`. Invalid mandatory values fail during bootstrap with the exact configuration key. Custom transports remain explicit provider starters and never require changes to the core.

## Two protocol runtimes

UpperHost treats these as different first-class workloads:

1. **Request/response** — parameter reads, commands, instrument control, PLC-style exchanges.
2. **Streaming** — telemetry, waveform acquisition, cameras and continuously emitted device data.

Do not force low-rate request/response devices through a high-rate streaming pipeline.

## Transport boundary

`ITransport` carries bytes and knows only how to open, close, send and receive. It must not know business commands. Modbus, SCPI, OPC UA and custom binary/ASCII semantics belong above it.

## Backpressure

`FanOutHub<T>` gives each consumer its own bounded channel. A UI consumer can use drop-oldest semantics while a lossless storage path can use wait semantics in a separate hub/pipeline. Backpressure policy is therefore explicit rather than accidental.

## Events and alarms

The typed `IEventBus` decouples modules without static global events. `IAlarmService` provides a generic raise/acknowledge/clear lifecycle. Product code supplies alarm rules; the platform supplies lifecycle and presentation seams.

## Plugins

`UpperHost.Modules` loads trusted in-process modules implementing `IUpperHostModule`. Plugins are not a security sandbox. Only load assemblies from trusted deployment locations.

## Presentation

The core does not depend on WPF. `UpperHost.Presentation.Wpf` is an adapter and includes metadata-driven controls for device lists, parameters, commands and alarms. New presentation stacks can reuse the same device, protocol, workflow and diagnostics layers.

## Testing

The simulator transport is a production-grade development seam, not a demo shortcut. `UpperHost.Testing` adds deterministic fault injection for latency, loss and failures so communication behavior can be exercised without physical hardware.
