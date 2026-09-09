# UpperHost 设备控制示例

[English](README.md) | 简体中文

这个可运行的 WPF 示例使用“模拟温控器”展示 UpperHost 的 **Control 控制路线**。它刻意不引入 Streaming 或波形概念，用来证明 UpperHost 不是采集框架。

## 完整链路

```text
WPF 适配层
  -> TemperatureControllerDevice
  -> 强类型 TemperatureControllerCommand
  -> TemperatureControllerProtocol
  -> RequestResponseClient
  -> SimulatorTransport
  -> TemperatureControllerSimulator
  -> Response / Readback
```

Simulator 表示模拟硬件，而不是 Device 领域对象。未来把 Simulator 换成 TCP、Serial 或厂商 SDK 时，WPF 不需要知道帧格式和字节协议。

## 示例包含

- DI 注册的 `IDevice` 随 UpperHost Host 生命周期自动进入 `IDeviceRegistry`。
- Connect / Disconnect 状态。
- 强类型 Read / Start / Stop 命令。
- 可写目标温度参数。
- Write 后执行独立 Readback 验证。
- 非法参数和设备拒绝命令以应用错误暴露。
- WPF 层不拼协议 `byte[]`。

## 运行

需要 Windows 和 .NET 10 SDK。

```powershell
dotnet run --project samples/UpperHost.Sample.DeviceControl/UpperHost.Sample.DeviceControl.csproj
```

操作：

1. 点击 **Connect**。
2. 点击 **Read status**。
3. 输入目标温度（例如 `30`），点击 **Set + readback**。
4. 点击 **Start control**，多次读取状态，可以看到模拟实际温度逐步接近目标值。
5. 点击 **Stop control**，最后 **Disconnect**。

## 应该学到什么

- `Device` 负责设备语义和设备状态。
- `Protocol` 负责命令编码、分帧和响应解码。
- `Transport` 只负责字节传输。
- `Simulator` 模拟真实硬件行为。
- WPF 只是 Presentation Adapter。

发送成功不等于物理动作完成；参数写入通过单独 Readback 验证设备真实值。
