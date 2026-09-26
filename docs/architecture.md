# OpenDeviceStudio architecture

English | [简体中文](architecture.zh-CN.md)

OpenDeviceStudio is an **enterprise-grade .NET upper-computer development scaffold for industrial device control, automation, and data acquisition**. It provides reusable runtime modules, conventions, providers, testing seams and a runnable source application scaffold for building product-specific industrial upper-computer applications while keeping product semantics and UI in the consuming application.

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

Cross-cutting scaffold/runtime capabilities are Configuration, Hosting/DI, Observability (structured logging, metrics, tracing and health), Dataflow, Persistence providers, Alarms, Diagnostics, Plugins and Testing.

## Architecture style: modular monolith

OpenDeviceStudio is a modular monolith by default. Modules are independently understandable and testable, but they compose into one application/process unless a separately approved distributed boundary is required.

Dependency direction is inward:

```text
Product App / Samples / Presentation
             |
          Starters
             |
 Hosting / application composition
             |
 Platform modules (Control / Workflows / Dataflow / ...)
             |
 Protocols + Providers / Adapters
             |
        Abstractions
```

This diagram describes dependency intent, not a requirement that every module reference every layer.

Hard rules:

- `OpenDeviceStudio.Abstractions` is the stable dependency root and references no repository project.
- Reusable production modules never reference the product App, Samples or Tests.
- Non-presentation production modules never reference `OpenDeviceStudio.Presentation.*`.
- Production modules never depend back on `OpenDeviceStudio.Starters`; Starters composes modules.
- Project reference cycles are forbidden.
- Cross-module behavior uses explicit public contracts/capabilities/events rather than shared mutable globals or another module's internal implementation.
- Providers/adapters own vendor dependencies and infrastructure details.
- A new project/module must represent a durable responsibility boundary, not merely a folder split.

These rules are executable through `scripts/validate_architecture.py`.

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

`AddOpenDeviceStudioApplication()` installs the platform defaults and selects a baseline transport from `OpenDeviceStudio:Transport:Type`. Invalid mandatory values fail during bootstrap with the exact configuration key. Custom transports remain explicit provider starters and never require changes to the core.

## Connection ownership

Physical connection lifetime is coordinated by `IConnectionManager`. A connection is identified by a stable `ConnectionId` and `TransportEndpoint`, and consumers acquire either shared or exclusive leases.

The canonical starter pipeline is:

```text
Provider/raw Transport
  -> ConnectionManagedTransport
  -> optional Resilience/ReconnectingTransport
  -> ObservedTransport
  -> Device / Protocol / Application consumer
```

The manager opens the underlying transport for the first lease and closes it after the last lease is released. Exclusive connections reject concurrent leases. A lease exposes data transfer but does not allow consumers to bypass the manager by directly opening or closing the physical transport.

The DI container owns the transport object lifetime; `IConnectionManager` owns the physical Open/Close handle lifecycle. This prevents double-disposal while keeping shutdown deterministic. Device, Workflow and Presentation code must not create or cache competing physical Serial/TCP/USB/native handles when using the managed runtime path.

Connection lifecycle tracing may contain `ConnectionId` for correlation. Metrics must stay low-cardinality and therefore must not use connection/session/request identifiers as metric attributes.

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

## Acquisition Session authority

OpenDeviceStudio.Acquisition provides the route-level lifecycle authority. AcquisitionSessionManager is host-owned; each running AcquisitionSession owns one bounded lifecycle supervisor. The coordinator freezes topology/configuration, prepares Required components before Sources, enforces the RequiredReady barrier before Source.StartAsync, converges the first Required fault, isolates Optional faults by default, and emits one terminal result.

The lifecycle supervisor is control-plane only. It does not carry Raw blocks. The hot path remains direct and bounded:

```text
Source callback / reader
 -> canonicalize
 -> Raw recorder ingress accepted
 -> processing handoff
 -> router branches
```

RawFirstAcquisitionIngress enforces the Raw-accepted-before-processing invariant without moving DSP, WPF or storage implementation into the Session coordinator. Replay is a new processing session over a read-only Raw source artifact and gets a new ProcessingEpoch. Multi-source isolation is allowed only when an isolation observer propagates the failed SourceId/connection epoch into dependent quality/processing state.

### Canonical RawData recording

Live acquisition defaults to a Required Canonical Raw recorder. `CanonicalRawBlock` owns an immutable copy of the device's original numeric/byte representation after framing/minimal decode and before calibration, filtering, algorithms or display downsampling. `RawRecorderAcceptResult.Accepted` means the bounded recorder responsibility domain owns the block; it does not mean the bytes have already been flushed to disk.

The default `OpenDeviceStudio.Storage.FileSystem` adapter uses a bounded no-drop channel, a dedicated writer, per-source/connection-epoch Apache Arrow IPC segments, SHA-256 integrity evidence, crash-safe manifest replacement and report-only recovery scanning. Active segments use `.partial`; only finalized segments are renamed to `.arrow`. The manifest state, generation, segment inventory, sequence diagnostics and durability policy determine whether an artifact is complete.

`SupportsBackpressure` sources may await bounded recorder capacity. `CannotBackpressure` sources are forced through the non-blocking `TryAccept` path; saturation faults the Required Raw path rather than silently dropping data. File-system details remain behind `IRawRecorder`, so products can replace the default adapter with EDF/EDF+, Parquet, HDF5, vendor-native or database storage without changing the Acquisition Session authority.

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

USB, CAN, BLE, vendor SDK adapters, storage backends and industry protocols should be delivered as independent providers/starters/modules. Vendor dependencies must not leak into `OpenDeviceStudio.Abstractions`.

`OpenDeviceStudio.Modules` loads trusted in-process modules implementing `IOpenDeviceStudioModule`. Plugins are not a security sandbox. Only load assemblies from trusted deployment locations.

## Presentation

The core does not depend on WPF. `OpenDeviceStudio.Presentation.Wpf` is an adapter and includes metadata-driven controls for device lists, parameters, commands and alarms. New presentation stacks can reuse the same device, protocol, workflow and diagnostics layers.

## Testing

The simulator transport is a production-grade development seam, not a demo shortcut. `OpenDeviceStudio.Testing` adds deterministic fault injection for latency, loss and failures so communication behavior can be exercised without physical hardware.

## Device package extensibility

`DevicePackageDescriptor` is the stable discovery surface for reusable device integrations. The contract lives in `OpenDeviceStudio.Abstractions`; `OpenDeviceStudio.Hosting` owns the in-process `IDevicePackageCatalog` implementation and startup registration lifecycle. This preserves the modular-monolith dependency direction while allowing the product application, samples and product-specific tooling to discover installed capabilities without reaching into provider internals.

Typed package configuration is represented by `DeviceConfigurationSchema<TConfiguration>`. Schema metadata is inspectable, while validation remains strongly typed and supports required/range/allowed-value rules plus explicit cross-field validation. Secret-bearing fields use reference metadata only.

Catalog conflict resolution is deterministic: one active descriptor exists per package id, the highest package version is active, and an ambiguous same-id/same-version registration is rejected. The catalog is limited to in-process descriptor discovery and registration; package acquisition, device integration and product UI remain responsibilities of the consuming application and its extension packages.
