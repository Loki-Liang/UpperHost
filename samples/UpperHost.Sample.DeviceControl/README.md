# UpperHost Device Control Sample

English | [简体中文](README.zh-CN.md)

This runnable WPF sample demonstrates the **control** path of UpperHost with a simulated temperature controller. It intentionally does not use streaming or waveform concepts.

## What it proves

```text
WPF adapter
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

- DI-provided `IDevice` is registered in `IDeviceRegistry` by the UpperHost host lifecycle.
- Connect / disconnect state.
- Typed read/start/stop commands.
- Editable target-temperature parameter.
- Write followed by explicit readback verification.
- Device-rejected values and command failures surface as application errors.
- WPF code never builds protocol bytes.

## Run

Windows and .NET 10 SDK are required.

```powershell
dotnet run --project samples/UpperHost.Sample.DeviceControl/UpperHost.Sample.DeviceControl.csproj
```

Then:

1. Click **Connect**.
2. Click **Read status**.
3. Enter a target such as `30` and click **Set + readback**.
4. Click **Start control** and repeatedly read status; the simulated actual temperature approaches the target.
5. Click **Stop control** and **Disconnect**.

## Architecture lesson

- `Device` owns device semantics and state.
- `Protocol` owns command encoding and response framing/decoding.
- `Transport` owns byte movement only.
- `Simulator` emulates hardware behavior.
- WPF is a presentation adapter.

A successful send is not treated as physical completion. Parameter writes are verified by a separate readback.
