# OpenDeviceStudio Device Control Sample

English | [简体中文](README.zh-CN.md)

This runnable WPF sample demonstrates the **control** path of OpenDeviceStudio with a simulated temperature controller. It intentionally does not use streaming or waveform concepts.

## What it proves

```text
WPF adapter
  -> BoundedCommandDispatcher
  -> TemperatureControllerDevice
  -> typed TemperatureControllerCommand
  -> TemperatureControllerProtocol
  -> RequestResponseClient
  -> SimulatorTransport
  -> TemperatureControllerSimulator
  -> response / readback
```

The simulator represents hardware, not the device domain object. Replacing it with TCP/Serial/vendor SDK transport does not require the WPF layer to understand framing or bytes.

## Features

- DI-provided `IDevice` is registered in `IDeviceRegistry` by the OpenDeviceStudio host lifecycle.
- Connect / disconnect state.
- Typed read/start/stop commands through the host-owned bounded dispatcher.
- Device resource arbitration and explicit read-vs-mutation safety metadata.
- Editable target-temperature parameter.
- Write followed by explicit readback verification.
- Device-rejected values and command failures surface as application errors.
- WPF code never builds protocol bytes.

## Run

Windows and .NET 10 SDK are required.

```powershell
dotnet run --project samples/OpenDeviceStudio.Sample.DeviceControl/OpenDeviceStudio.Sample.DeviceControl.csproj
```

Then:

1. Click **Connect**.
2. Click **Read status**.
3. Enter a target such as `30` and click **Set + readback**.
4. Click **Start control** and repeatedly read status; the simulated actual temperature approaches the target.
5. Click **Stop control** and **Disconnect**.

## Architecture lesson

- `BoundedCommandDispatcher` owns bounded admission, resource arbitration and command lifecycle before Device execution.
- `Device` owns device semantics and state.
- `Protocol` owns command encoding and response framing/decoding.
- `Transport` owns byte movement only.
- `Simulator` emulates hardware behavior.
- WPF is a presentation adapter.

A successful send is not treated as physical completion. Parameter writes are verified by a separate readback.

## Device package catalog

The sample also publishes a `DevicePackageDescriptor` for the simulated temperature controller. Startup validates `TemperatureControllerPackageOptions` before device construction, registers the descriptor through the host lifecycle, and the window resolves `IDevicePackageCatalog` to show the active package/version. This is the reference consumption path for Issue #22 and demonstrates how an application composes reusable catalog infrastructure.


## Authoritative control state

The reference path now uses:

```text
Polling / Command / Parameter Readback
        -> DeviceSnapshotStore
        -> WPF read model
```

Target writes use `TypedParameterRuntime<double>` and the same resource arbiter as commands and polling. Reconnect starts a new connection epoch and a required rehydrate barrier. The sample intentionally uses Exclusive polling access because its Provider has not declared a concurrent-read capability; `SharedRead` is opt-in only for Providers that prove it is safe.
