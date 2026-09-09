# UpperHost.Transport.Can

[简体中文](README.zh-CN.md) | English

`UpperHost.Transport.Can` preserves CAN/CAN FD frame boundaries through `IMessageTransport<CanFrame>`.

`CanFrame` carries arbitration ID, standard/extended ID mode, CAN FD flag, bit-rate switch, payload and optional timestamp. It validates identifier and payload limits before a frame reaches the driver backend.

## Integration

Implement `ICanBackend` using the product's CAN stack (for example a Windows vendor driver, SocketCAN bridge or hardware SDK), then construct:

```csharp
var transport = new CanTransport(
    new ProductCanBackend(),
    new CanTransportOptions("can0", NominalBitRate: 500_000, DataBitRate: 2_000_000));
```

The backend owns driver calls. Protocol/device code owns application semantics such as interpreting arbitration IDs as domain messages.

Do not flatten a CAN frame into an arbitrary byte stream; arbitration metadata is part of the transport message.
