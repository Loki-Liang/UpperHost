# UpperHost scaffold positioning

[简体中文](scaffold.zh-CN.md) | English

UpperHost is a **general-purpose .NET upper-computer / device-application development scaffold**.

Its job is to give developers a reusable engineering baseline for building device-facing desktop applications without rebuilding the same communication, lifecycle, testing, diagnostics and extension infrastructure for every product.

## What the scaffold provides

UpperHost provides reusable building blocks and conventions for three peer application paths:

- **Control** — commands, parameters, readback, state, interlocks and diagnostics.
- **Automation** — multi-device coordination, state machines, workflows, scheduling, alarms and recovery.
- **Acquisition** — continuous streams, backpressure, storage, algorithms and presentation.

The scaffold includes:

- a modular-monolith runtime;
- Device + Capability contracts;
- Transport and Protocol seams;
- command/control runtime;
- streaming/dataflow primitives;
- workflow/state-machine runtime APIs;
- diagnostics, alarms, events, storage and resilience helpers;
- Serial/TCP/Simulator and extensible provider seams;
- WPF presentation adapters and reusable controls;
- deterministic Simulator/fault-injection support;
- automated tests and architecture governance;
- NuGet-ready modules;
- `dotnet new upperhost` project templates;
- runnable reference samples.

## What UpperHost is not

UpperHost is **not**:

- a finished universal end-user Workbench/HMI product;
- a no-code or low-code platform;
- a visual flow/node editor;
- an industry-specific medical/PLC/robot framework;
- a promise that every device works without a device/protocol/provider integration.

A product created with UpperHost still owns its device semantics, protocol details, command/interlock policy, application workflow and product-specific UI.

## Primary developer experience

The preferred development path is:

```text
dotnet new upperhost
        |
        v
generated upper-computer application
        |
        +-- product Device capabilities
        +-- product Protocol / Provider
        +-- product control / acquisition behavior
        +-- product UI
        |
        v
build / test / package
```

Developers should extend the scaffold rather than fork or rewrite its runtime.

## Architecture baseline

UpperHost remains a **modular monolith by default**. Modules have explicit responsibilities and dependency direction, while the generated product normally runs as one application/process.

The scaffold must not introduce microservices, remote RPC, or distributed consistency without an explicit product/operational requirement.

## Presentation boundary

WPF is the current default presentation adapter and project template, not the definition of UpperHost itself. Other presentation stacks may be added through the same runtime contracts.

A future reference shell or Workbench may demonstrate the scaffold, but it must remain a consumer of UpperHost rather than become a second runtime or redefine the repository as an end-user low-code product.

## Delivery rule

A scaffold feature is complete only when the reusable implementation, tests, documentation and generated/consumer usage stay aligned. Hardware-specific support must distinguish Simulator/contract evidence from real-hardware validation.
