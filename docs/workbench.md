# UpperHost Workbench product boundary

[简体中文](workbench.zh-CN.md) | English

UpperHost Workbench is the primary end-user product surface for UpperHost. It provides an open-box device application experience on top of the same modular-monolith runtime used by code-first applications.

## Product goal

A supported-device user should work through a stable device-application path:

```text
Project
  -> Device
  -> Connection
  -> Command / Parameter / Streaming
  -> Data / Waveform / Alarm / Diagnostics
  -> Save
  -> Reopen
  -> Run again
```

The Workbench is not a generic low-code platform and is not a second runtime.

## Explicit non-goal: visual workflow orchestration

UpperHost does **not** build a visual workflow editor, node graph, drag-and-drop programming surface, or generic low-code flow engine.

The existing Workflow and StateMachine runtimes remain valid code/API capabilities for automation logic, but they are not expanded into a graphical orchestration product surface.

The following are therefore out of scope:

- flow/node editor;
- visual DAG authoring;
- drag-to-connect execution graphs;
- workflow marketplace;
- generic low-code expression runtime;
- visual code generation from a graph.

If a future task proposes one of these capabilities, it requires a new product decision and explicit change to this boundary.

## Application composition root

The desktop product lives in `apps/UpperHost.Workbench.Wpf`. It is an application composition root, not a reusable platform module:

- it may compose Starters, Presentation and application services;
- `src/` production modules must never depend back on `apps/`;
- reusable WPF controls remain in `UpperHost.Presentation.Wpf`;
- reusable Workbench application state lives in `UpperHost.Workbench.Application`.

## Workbench scope

The Workbench may provide direct configuration and operation for:

- projects and project lifecycle;
- device catalog/templates;
- connection configuration and ownership;
- device status;
- commands and command results;
- parameters and readback;
- streaming data monitoring;
- waveform/numeric/table views;
- alarms;
- logs;
- diagnostics/health;
- storage configuration;
- Simulator/Fault profiles.

These are device-application surfaces, not workflow-authoring surfaces.

## User types

### Configuration user

Uses supported device templates and Workbench screens without modifying platform source.

### Device integration developer

Adds a new Device/Protocol/Provider/Simulator package when a private protocol or vendor SDK is not already supported.

### Product developer

Builds a product-specific application on UpperHost NuGet packages and templates while preserving the same runtime contracts.

## Delivery surfaces

- **Workbench**: primary end-user product.
- **Runtime/NuGet**: versioned platform modules and extension contracts.
- **dotnet new templates**: code-first second-development entry.
- **Samples**: executable reference behavior, not a separate product runtime.

## Single-runtime rule

Workbench, templates, tests and product applications must consume the same Device/Control/Protocol/Dataflow/Diagnostics contracts. Do not create Workbench-only duplicate device, command, connection or streaming runtimes.

## Definition of done for Workbench features

A Workbench capability is not complete merely because a control or API exists. Where applicable, delivery includes:

- production implementation;
- risk-matched automated tests;
- Simulator path;
- visible error/fault state;
- persistence/reopen behavior when configuration is involved;
- Windows CI evidence;
- documentation;
- merge to `main`.

Hardware-specific claims require separate hardware validation evidence; Simulator or loopback does not count as real hardware validation.
