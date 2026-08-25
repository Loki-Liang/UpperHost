# UpperHost architecture

UpperHost is a **general device-application platform**, not a framework for one industry or one hardware category.

## Architectural rule

The stable core models the recurring structure of a host application; product-specific device semantics remain outside the core.

```text
Presentation (WPF / WinUI / Avalonia / CLI / Service)
        |
Application / Workflow / State Machine
        |
Device + Capabilities
        |
Protocol (request-response or streaming)
        |
Transport (Serial / TCP / USB / CAN / BLE / ...)
```

Cross-cutting platform capabilities are Configuration, Hosting/DI, Dataflow, Persistence providers, Diagnostics, Plugins and Testing.

## Device model

`IDevice` is intentionally small. Features are expressed through capability interfaces such as `IConnectable`, `IConfigurable<T>`, `ICommandable<TCommand,TResult>`, `ICalibratable<,>`, `IParameterProvider` and `IDataSource<T>`.

This avoids forcing a PLC, camera, motor controller, laboratory instrument and biosignal acquisition unit into one inheritance tree.

## Two protocol runtimes

UpperHost treats these as different first-class workloads:

1. **Request/response** — parameter reads, commands, instrument control, PLC-style exchanges.
2. **Streaming** — telemetry, waveform acquisition, cameras and continuously emitted device data.

Do not force low-rate request/response devices through a high-rate streaming pipeline.

## Transport boundary

`ITransport` carries bytes and knows only how to open, close, send and receive. It must not know business commands. A Modbus/SCPI/custom binary codec belongs above it.

## Backpressure

`FanOutHub<T>` gives each consumer its own bounded channel. A UI consumer can use drop-oldest semantics while a lossless storage path can use wait semantics in a separate hub/pipeline. Backpressure policy is therefore explicit rather than accidental.

## Plugins

`UpperHost.Modules` loads trusted in-process modules implementing `IUpperHostModule`. Plugins are not a security sandbox. Only load assemblies from trusted deployment locations.

## Presentation

The core does not depend on WPF. `UpperHost.Presentation.Wpf` is an adapter. New presentation stacks can reuse the same device, protocol, workflow and diagnostics layers.

## Testing

The simulator transport is a production-grade development seam, not a demo shortcut. `UpperHost.Testing` adds deterministic fault injection for latency, loss and failures so communication behavior can be exercised without physical hardware.
