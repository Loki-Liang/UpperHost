# UpperHost architecture

English | [简体中文](architecture.zh-CN.md)

UpperHost is a **general device-application platform**, not a framework for one industry, hardware category or acquisition workload.

## Stable platform boundary

```text
Presentation (WPF / WinUI / Avalonia / CLI / Service)
        |
Application / Workflow / State Machine / Event Bus
        |
Device Registry + Discovery + Capability Composition
        |
+---------------------+---------------------+
| Command / Parameter |      Streaming      |
| Request / Response  | Dataflow/Backpressure|
+---------------------+---------------------+
        |
Protocol
        |
Transport (Serial / TCP / USB / CAN / BLE / Vendor SDK / ...)
        |
Hardware
```

Cross-cutting platform capabilities are Configuration, Hosting/DI, Dataflow, Persistence providers, Alarms, Diagnostics, Plugins and Testing.

## Three first-class application paths

### Control

PLCs, servo drives, temperature controllers, power supplies, laboratory instruments and actuators primarily use commands, parameters, acknowledgement, completion conditions, readback, state validation, interlocks and diagnostics.

### Automation

Stations and machines coordinating multiple devices primarily use capabilities, state machines, workflows, alarms and recovery.

### Acquisition

DAQ, biosignal devices, cameras, sensors and telemetry primarily use continuous streaming, backpressure, fan-out, storage, algorithms and presentation.

The three paths share the same stable Device/Protocol/Transport boundaries. None may become an industry assumption in Core.

## Device model

`IDevice` is intentionally small. Features are expressed through capability interfaces such as `IConnectable`, `IConfigurable<T>`, `ICommandable<TCommand,TResult>`, `ICalibratable<,>`, `IParameterProvider` and `IDataSource<T>`.

This avoids forcing a PLC, camera, motor controller, laboratory instrument and biosignal acquisition unit into one inheritance tree.

Any `IDevice` registered in DI is automatically added to `IDeviceRegistry` when the Generic Host starts. `IDeviceDiscoveryService` aggregates any installed `IDeviceDiscoverer` providers without requiring the core to know vendor discovery protocols.

## Auto-configuration

`AddUpperHostApplication()` installs the platform defaults and selects a baseline transport from `UpperHost:Transport:Type`. Invalid mandatory values fail during bootstrap with the exact configuration key. Custom transports remain explicit provider starters and never require changes to the core.

## Command and request/response runtime

Control workloads normally follow this path:

```text
Domain command
  -> guard / state validation
  -> encoder
  -> transport send
  -> decoder
  -> ack / result
  -> completion condition / readback
```

A successful send means bytes were handed to the transport; it does not prove physical completion. Products own completion semantics, retries and whether readback is mandatory.

Software command guards and interlocks are application safety constraints. They do not replace hardware emergency stops, safety relays, safety PLCs or certified safety circuits.

## Streaming runtime

Streaming workloads follow a separate first-class path:

```text
Transport receive
  -> decoder
  -> typed stream
  -> dataflow
  -> storage / algorithm / presentation
```

Do not force low-rate request/response devices through a high-rate streaming pipeline.

## Transport boundary

`ITransport` carries bytes and knows only how to open, close, send and receive. It must not know business commands. Modbus, SCPI, OPC UA and custom binary/ASCII semantics belong above it.

## Protocol boundary

Protocol code converts domain commands to bytes and incoming bytes to domain messages. Framing state belongs to the decoder, which must handle fragmentation, multiple messages per receive chunk, invalid frames and recovery.

## Backpressure

`FanOutHub<T>` gives each consumer its own bounded channel. A UI consumer can use drop-oldest semantics while a lossless storage path can use wait semantics in a separate hub/pipeline. Backpressure policy is therefore explicit rather than accidental.

## State machines and workflows

State machines answer **what states and transitions are valid now**. Workflows answer **what step happens next**.

A typical automation workflow might be:

```text
connect -> self-test -> configure -> home -> run -> stop
```

Each step delegates hardware details to device capabilities/services rather than manipulating sockets or protocol buffers directly.

## Events and alarms

The typed `IEventBus` decouples modules without static global events. `IAlarmService` provides a generic raise/acknowledge/clear lifecycle. Product code supplies alarm rules; the platform supplies lifecycle and presentation seams.

## Providers and plugins

USB, CAN, BLE, vendor SDK adapters, storage backends and industry protocols should be delivered as independent providers/starters/modules. Vendor dependencies must not leak into `UpperHost.Abstractions`.

`UpperHost.Modules` loads trusted in-process modules implementing `IUpperHostModule`. Plugins are not a security sandbox. Only load assemblies from trusted deployment locations.

## Presentation

The core does not depend on WPF. `UpperHost.Presentation.Wpf` is an adapter and includes metadata-driven controls for device lists, parameters, commands and alarms. New presentation stacks can reuse the same device, protocol, workflow and diagnostics layers.

## Testing

The simulator transport is a production-grade development seam, not a demo shortcut. `UpperHost.Testing` adds deterministic fault injection for latency, loss and failures so communication behavior can be exercised without physical hardware.
