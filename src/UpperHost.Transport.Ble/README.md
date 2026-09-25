# UpperHost.Transport.Ble

[简体中文](README.zh-CN.md) | English

`UpperHost.Transport.Ble` models BLE as GATT messages, not as a fake serial byte stream.

A `BleMessage` preserves service UUID, characteristic UUID, value and direction/type. `BleTransport` sends `Write` messages and receives `Notification` messages through `IMessageTransport<BleMessage>`.

## Integration

Implement `IBleBackend` with the platform BLE stack used by your product:

```csharp
var transport = new BleTransport(
    new ProductBleBackend(),
    new BleTransportOptions("device-id-or-address"));
```

The backend owns discovery, connection, characteristic subscription and platform API calls. Protocol/device code owns the meaning of characteristic values.

Do not hide characteristic identity inside an arbitrary byte stream; service and characteristic UUIDs are part of the transport contract.
