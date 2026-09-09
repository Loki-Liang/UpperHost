# Transport providers

[简体中文](providers.zh-CN.md) | English

UpperHost supports more than byte-stream transports. Provider design must preserve the native communication model instead of forcing every technology through one abstraction.

## Choose the correct seam

| Technology shape | UpperHost seam | Examples |
| --- | --- | --- |
| Ordered byte stream | `ITransport` | Serial, TCP, USB bulk endpoints, byte-oriented vendor SDKs |
| Discrete frames/messages | `IMessageTransport<TMessage>` | CAN frames, BLE GATT operations/notifications, datagrams |
| Vendor SDK already exposes domain operations | Device capability directly | Camera `Capture`, robot `Move`, instrument `Measure` |

`IMessageTransport<TMessage>` shares lifecycle semantics with `ITransport`, but it preserves message boundaries. `TMessage` belongs to the provider package, not `UpperHost.Abstractions`.

## Provider package rules

1. Keep native/vendor dependencies inside the provider package.
2. Core must not reference libusb, CAN drivers, BLE stacks or vendor SDK assemblies.
3. Transport/provider code handles connection and transfer mechanics, not product business commands.
4. Protocol/device code owns semantic decoding and domain operations.
5. Make cancellation, timeout and disconnect behavior explicit.
6. Expose stable backend seams so native libraries can be substituted in tests.
7. Do not label software priority, cancellation or disconnect as a hardware emergency stop.

## P3 packages

- `UpperHost.Transport.Usb`: byte-oriented USB transport backed by an injected USB channel implementation.
- `UpperHost.Transport.Can`: CAN frame transport implementing `IMessageTransport<CanFrame>`.
- `UpperHost.Transport.Ble`: BLE GATT message transport implementing `IMessageTransport<BleMessage>`.
- `UpperHost.Transport.VendorSdk`: adapter pattern for byte-oriented vendor SDK sessions plus guidance for SDKs that should map directly to Device capabilities.

Each concrete provider is delivered in its own GitHub Flow PR with build/test evidence.
