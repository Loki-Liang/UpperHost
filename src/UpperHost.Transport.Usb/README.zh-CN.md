# UpperHost.Transport.Usb

简体中文 | [English](README.md)

`UpperHost.Transport.Usb` 用于把**字节型 USB Endpoint** 接入 UpperHost `ITransport`，同时禁止 Core 直接依赖 WinUSB、libusb、FTDI 或厂商 Native DLL。

## 接入方式

产品根据实际 USB 技术栈实现 `IUsbByteChannel`，然后创建 `UsbTransport`：

```csharp
var options = new UsbTransportOptions(
    VendorId: 0x1234,
    ProductId: 0x5678,
    InterfaceNumber: 0,
    InEndpoint: 0x81,
    OutEndpoint: 0x01);

IUsbByteChannel channel = new ProductUsbChannel();
ITransport transport = new UsbTransport(channel, options);
```

只有 `ProductUsbChannel` 可以引用 WinUSB/libusb/厂商 DLL API。

## 边界

只有设备本身提供字节型 USB Endpoint 时才使用该 Provider。如果厂商 SDK 已经直接提供 `StartCapture`、`ReadMeasurement` 等领域 API，应直接实现 Device Capability，不要为了套 `ITransport` 把领域调用伪装成 byte[]。
