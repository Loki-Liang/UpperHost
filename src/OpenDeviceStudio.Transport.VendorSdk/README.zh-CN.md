# OpenDeviceStudio.Transport.VendorSdk

简体中文 | [English](README.md)

`OpenDeviceStudio.Transport.VendorSdk` 是**厂商 SDK 扩展的统一参考模板**。它只用于厂商 SDK 提供“有序字节会话”、但又不适合直接归类为 Serial/TCP/USB 的情况。

该包故意不引用任何厂商 DLL。产品代码通过 `IVendorByteSession` 隔离 Native Handle、Callback、线程亲和性和厂商错误码，Core 不感知具体 SDK。

## 字节型 SDK 模板

```csharp
var options = new VendorSdkTransportOptions(
    VendorName: "Acme",
    DeviceAddress: "SN-001");

IVendorByteSession session = new AcmeSdkByteSession();
ITransport transport = new VendorSdkTransport(session, options);
```

只有 `AcmeSdkByteSession` 这一层可以引用厂商 SDK DLL / Native Library。

## 先判断 SDK 属于哪一类

不要因为硬件“带 SDK”就一律套 `VendorSdkTransport`。

| 厂商 SDK 形态 | 正确的 OpenDeviceStudio seam |
| --- | --- |
| 有序原始字节 | `IVendorByteSession` + `VendorSdkTransport` |
| 离散 Frame / Message | Provider 自己定义消息类型 + `IMessageTransport<TMessage>` |
| 已提供 `Capture`、`MoveTo`、`Measure` 等领域动作 | 直接实现 Device Capability |

如果 SDK 已经是领域 API，再把动作伪装成 byte[] 会丢失语义，也会让状态、完成判定和测试边界变差。

## Provider 实现检查表

1. 厂商/Native 依赖只允许存在于产品 Provider 包。
2. Native Callback 转入 OpenDeviceStudio 时不能阻塞厂商回调线程。
3. 明确 Native Handle 所有权，并在 `DisposeAsync` 中确定性释放。
4. Cancellation、Timeout、Disconnect、厂商错误码必须显式映射。
5. 非字节型 SDK 必须保留 Frame/Message Identity。
6. 接真实硬件前先用 Fake Backend/Session 做自动化测试。
7. 软件 Disconnect/Cancel 不能宣称等价于硬件急停。

真实产品通常应建立 `Company.Transport.Acme`、`Company.Device.AcmeCamera` 等厂商专用包；本包负责提供可编译、可测试、可复制的统一参考模板。
