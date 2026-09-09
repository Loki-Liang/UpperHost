# UpperHost.Transport.Usb

[简体中文](README.zh-CN.md) | English

`UpperHost.Transport.Usb` adapts byte-oriented USB endpoints to UpperHost `ITransport` without coupling Core to WinUSB, libusb, FTDI or another native library.

## Integration

Implement `IUsbByteChannel` with the USB stack used by your product, then construct `UsbTransport`:

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

`ProductUsbChannel` is the only layer that should reference WinUSB/libusb/vendor DLL APIs.

## Boundary

Use this provider only when the device exposes a byte-oriented USB endpoint. If the vendor SDK already exposes domain operations such as `StartCapture` or `ReadMeasurement`, implement Device capabilities directly instead of converting those calls into fake bytes.
