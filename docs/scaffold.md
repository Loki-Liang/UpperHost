# UpperHost scaffold positioning

[简体中文](scaffold.zh-CN.md) | English

UpperHost is an **enterprise-grade .NET upper-computer development scaffold for industrial device control, automation, and data acquisition**.

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

## Product ownership boundary

A product created with UpperHost owns its device semantics, protocol details, command/interlock policy, application workflow and product-specific UI. UpperHost owns the reusable engineering baseline: runtime contracts, providers, templates, testing seams, diagnostics and presentation adapters shared across products.

## Primary developer experience

UpperHost uses a **source-first secondary-development** model. Developers clone the repository and continue developing it directly as their upper-computer product:

```text
git clone UpperHost
        |
        v
run app/UpperHost.App
        |
        +-- product Device capabilities
        +-- product Protocol / Provider
        +-- product Workflow / State
        +-- product Acquisition
        +-- product UI
        |
        v
build / test / package the product
```

`app/UpperHost.App` is the canonical product entry point, `src/UpperHost.*` contains reusable infrastructure, and `samples/` contains reference implementations only.

Product developers do not need to build UpperHost itself, pack a project template, or generate a second repository before starting. Repository-level framework build/test work belongs to contributors changing the reusable runtime.

## Architecture baseline

UpperHost remains a **modular monolith by default**. Modules have explicit responsibilities and dependency direction, while the generated product normally runs as one application/process.

The scaffold must not introduce microservices, remote RPC, or distributed consistency without an explicit product/operational requirement.

## Presentation boundary

WPF is the current default presentation adapter and project template, not the definition of UpperHost itself. Other presentation stacks may be added through the same runtime contracts.

Reference presentation applications may demonstrate the scaffold, but they consume the same UpperHost runtime contracts and remain examples of how a product composes the reusable modules.

## Delivery rule

A scaffold feature is complete only when the reusable implementation, tests, documentation and generated/consumer usage stay aligned. Hardware-specific support must distinguish Simulator/contract evidence from real-hardware validation.
