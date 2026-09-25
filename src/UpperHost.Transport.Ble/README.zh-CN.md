# UpperHost.Transport.Ble

简体中文 | [English](README.md)

`UpperHost.Transport.Ble` 把 BLE 按 **GATT Message** 建模，而不是伪装成串口 byte stream。

`BleMessage` 保留 Service UUID、Characteristic UUID、Value 和消息类型。`BleTransport` 通过 `IMessageTransport<BleMessage>` 发送 `Write`，接收 `Notification`。

## 接入

产品使用实际平台 BLE Stack 实现 `IBleBackend`：

```csharp
var transport = new BleTransport(
    new ProductBleBackend(),
    new BleTransportOptions("device-id-or-address"));
```

Backend 负责扫描/连接、Characteristic 订阅和系统 API 调用；Protocol / Device 层负责解释 Characteristic Value 的业务语义。

禁止把 Characteristic Identity 藏进随意拼接的 byte stream，因为 Service/Characteristic UUID 本身就是 BLE 传输上下文的一部分。
