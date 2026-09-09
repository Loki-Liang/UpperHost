# DataAcquisition 数据采集示例

简体中文 | [English](README.md)

这个示例用于验证 UpperHost 的**数据采集一级路线**，而不是把采集重新变成整个框架的中心。

```text
SimulatorAcquisitionDevice (IDataSource<SampleFrame>)
        |
        v
   FanOutHub<SampleFrame>
      /             \
     v               v
显示 Consumer       存储 Consumer
慢速/降采样显示      JSON Lines
```

## 这个示例证明什么

- 采集设备通过 `IDataSource<T>` 暴露强类型异步数据流。
- `FanOutHub<T>` 把生产速度和多个 Consumer 解耦。
- 每个订阅者使用有界缓冲，并显式选择 `DropOldest` 背压/丢弃策略。
- 示例故意让显示端变慢，因此显示可以丢弃旧帧，但不会把 UI 渲染速度反向绑死采集设备。
- 存储是独立 Consumer seam；以后可以替换为 SQLite、Parquet、时序数据库或产品自己的存储实现，而不用修改 Device。
- 显示降采样与采集速率分离，高频采集禁止依赖 UI Timer 拉取数据。

## 运行

```powershell
dotnet run --project samples/UpperHost.Sample.DataAcquisition/UpperHost.Sample.DataAcquisition.csproj
```

Simulator 会生成 500 帧、4 通道数据。程序最后输出生产帧数、显示收到的帧数、显示观察到的序号缺口以及存储帧数，让背压行为可直接观察。

## 真实项目必须进一步明确

- Buffer Capacity；
- Wait / DropOldest / DropNewest 策略；
- 存储批量写入；
- 时间戳来源与多设备时钟同步；
- 断线重连；
- 文件切片/归档；
- 数据完整性；
- 哪些 Consumer 可以反向施加背压。

这些都是产品策略，不应被某个 EMG、DAQ 或摄像头业务硬编码进 Core。
