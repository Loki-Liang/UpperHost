# UpperHost.Transport.Can

简体中文 | [English](README.md)

`UpperHost.Transport.Can` 通过 `IMessageTransport<CanFrame>` 保留 CAN / CAN FD 原生 Frame 边界。

`CanFrame` 保留 Arbitration ID、Standard/Extended ID、CAN FD、BRS、Payload 与可选时间戳，并在进入 Driver Backend 前校验 ID 和 Payload 长度。

## 接入

产品使用自己的 CAN Driver/SDK 实现 `ICanBackend`，例如厂商 Windows Driver、SocketCAN Bridge 或硬件 SDK：

```csharp
var transport = new CanTransport(
    new ProductCanBackend(),
    new CanTransportOptions("can0", NominalBitRate: 500_000, DataBitRate: 2_000_000));
```

Backend 只负责 Driver 调用；Protocol / Device 层负责解释 Arbitration ID、命令码和业务语义。

禁止把 CAN Frame 随意压成连续 byte stream，因为 Arbitration ID 等元数据本身就是传输消息的一部分。
