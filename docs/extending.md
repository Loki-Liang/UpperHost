# Extending UpperHost

English | [简体中文](extending.zh-CN.md)

UpperHost is a source-first development scaffold: extensions should add reusable device/protocol/provider capabilities while product-specific behavior stays in `app/UpperHost.App`.

## Add a device

Implement `IDevice`, then implement only the capabilities the hardware actually supports. Register the device in DI and expose it through `IDeviceRegistry` after application startup.

Prefer composition and dedicated services over deep `BaseDevice` inheritance hierarchies.

## Add a transport

Implement `ITransport`. Keep the following out of the transport implementation:

- business commands;
- framing semantics;
- checksums/CRC;
- protocol-level retries;
- product state machines;
- UI updates.

New USB, CAN, BLE or vendor SDK connectivity should be delivered as independent provider/starter packages.

## Add a protocol

Implement `ICommandEncoder<TCommand>` and/or `IMessageDecoder<TMessage>`. The decoder owns framing state, so it must correctly handle fragmented input, multiple messages in one received chunk, half-frames, invalid frames and recovery after length/checksum failures.

## Add a control command

Prefer typed domain commands over building `byte[]` values inside UI code:

```text
MoveAbsolute(100)
SetTargetTemperature(80)
Capture()
StartTest()
```

A command execution path should make state validation, software guard/interlock checks, timeout, cancellation, acknowledgement, completion conditions and readback explicit.

Software guards/interlocks do not replace hardware emergency stops, safety PLCs or certified safety circuits.

## Add a workflow

Compose `IWorkflowStep` instances into a `WorkflowDefinition`. A workflow should describe product behavior such as:

```text
connect -> self-test -> configure -> home -> run -> stop
```

Each step delegates hardware details to capabilities/services instead of directly manipulating transports.

## Add a plugin

Implement `IUpperHostModule` and register services inside `ConfigureServices`. Deploy the assembly to a trusted plugin directory and call `AddModulesFromDirectory` during bootstrap.

Plugins are trusted in-process extensions, not a security sandbox.

## Start product development

Clone the repository and run the canonical product entry directly:

```powershell
git clone https://github.com/Loki-Liang/UpperHost.git MyMachine
cd MyMachine
dotnet run --project app/UpperHost.App/UpperHost.App.csproj
```

The default transport is `simulator`. Change `app/UpperHost.App/appsettings.json` to `serial` or `tcp` when a real endpoint is required. Additional providers should remain isolated behind the existing provider/starter boundaries.

If this is your first UpperHost application, start with [Getting Started](getting-started.md).

## Device packages, descriptors and catalog

A reusable device integration may publish a `DevicePackageDescriptor` in addition to its runtime `IDevice` implementation. The descriptor is runtime-neutral metadata for the product application, samples, CLI/tooling or product-specific UI. It describes package/device identity, package version, capabilities, supported transports, parameter/command/signal metadata, typed configuration schema, optional simulator/diagnostics metadata and provider/protocol dependencies.

Register a package at composition time:

```csharp
builder.AddDevicePackage(MyDevicePackage.Descriptor, new MyDevicePackageOptions());
```

`AddDevicePackage` validates the typed configuration immediately through the package schema. Required values, ranges, allowed values and package-defined cross-field rules report canonical configuration paths and fail before the device is built or I/O starts.

Resolve `IDevicePackageCatalog` after host startup to enumerate installed descriptors or filter them by capability, transport or vendor. For the same package id the highest `System.Version` wins independent of registration order; two different descriptors with the same package id and version fail with `DevicePackageConflictException`.

Secret fields must be modeled as `DeviceSecretReference`. The reference describes only the external source/key; plaintext credentials do not belong in package metadata, normal logs or persisted descriptor data.

The catalog is an in-process discovery and registration service. Package acquisition, private protocols, native SDKs, vendor drivers and product UI remain responsibilities of the consuming application and its provider/device packages.
