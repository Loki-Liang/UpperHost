# Transport providers

[简体中文](providers.zh-CN.md) | English

OpenDeviceStudio supports more than byte-stream transports. Provider design must preserve the native communication model instead of forcing every technology through one abstraction.

## Choose the correct seam

| Technology shape | OpenDeviceStudio seam | Examples |
| --- | --- | --- |
| Ordered byte stream | `ITransport` | Serial, TCP, USB bulk endpoints, byte-oriented vendor SDKs |
| Discrete frames/messages | `IMessageTransport<TMessage>` | CAN frames, BLE GATT operations/notifications, datagrams |
| Vendor SDK already exposes domain operations | Device capability directly | Camera `Capture`, robot `Move`, instrument `Measure` |

`IMessageTransport<TMessage>` shares lifecycle semantics with `ITransport`, but it preserves message boundaries. `TMessage` belongs to the provider package, not `OpenDeviceStudio.Abstractions`.

## Provider package rules

1. Keep native/vendor dependencies inside the provider package.
2. Core must not reference libusb, CAN drivers, BLE stacks or vendor SDK assemblies.
3. Transport/provider code handles connection and transfer mechanics, not product business commands.
4. Protocol/device code owns semantic decoding and domain operations.
5. Make cancellation, timeout and disconnect behavior explicit.
6. Expose stable backend seams so native libraries can be substituted in tests.
7. Do not label software priority, cancellation or disconnect as a hardware emergency stop.

## P3 packages

- `OpenDeviceStudio.Transport.Usb`: byte-oriented USB transport backed by an injected USB channel implementation.
- `OpenDeviceStudio.Transport.Can`: CAN frame transport implementing `IMessageTransport<CanFrame>`.
- `OpenDeviceStudio.Transport.Ble`: BLE GATT message transport implementing `IMessageTransport<BleMessage>`.
- `OpenDeviceStudio.Transport.VendorSdk`: adapter pattern for byte-oriented vendor SDK sessions plus guidance for SDKs that should map directly to Device capabilities.

## Vendor SDK decision template

Classify the vendor API before writing an adapter:

1. **Raw ordered bytes** → implement `IVendorByteSession` and use `VendorSdkTransport`.
2. **Discrete frame/message API** → define the provider-owned message type and implement `IMessageTransport<TMessage>`.
3. **Domain API** → implement Device capabilities directly; do not create fake transport bytes.

The vendor package owns native DLL references, handle lifetime, callback/thread-affinity rules and vendor error mapping. Core remains vendor-neutral. See [OpenDeviceStudio.Transport.VendorSdk](../src/OpenDeviceStudio.Transport.VendorSdk/README.md) for the reusable template and checklist.

Each concrete provider is delivered in its own GitHub Flow PR with build/test evidence.
