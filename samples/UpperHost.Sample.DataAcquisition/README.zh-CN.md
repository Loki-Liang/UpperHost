# DataAcquisition 数据采集示例

简体中文 | [English](README.md)

该示例现在首先验证 **#64 Acquisition Session 是唯一生命周期权威**。

```text
AcquisitionSessionManager
        |
        v
AcquisitionSession
  |              |
  | Required     | Source
  v              v
SampleDataflow <- SessionManagedSimulatorSource
  |                    |
  +--> Display          +--> SimulatorAcquisitionDevice
  +--> JSON Lines
```

## 这个示例证明什么

- AcquisitionSession 统一决定 Prepare / Ready / Running / Stop / Finalize / Terminal，Device、Dataflow、UI 不再各自猜 Session 状态。
- Required Dataflow 必须先准备完成，Source 才能启动；Required Finalize 成功后 Session 才允许进入 Completed。
- Source 自己持有 producer task，并通过 Session fault seam 上报运行期故障；Composition Root 不产生无人管理的 fire-and-forget task。
- Stop/退出路径明确，最终输出 Session terminal result。
- Display / Storage 与 Device 继续保持解耦。

## 重要边界

当前 FanOutHub + JSON Lines 仍是**旧数据路径示例**，不能被描述成生产级 Raw Recorder 或最终 Signal Pipeline。#58、#56、#59 继续分别负责无损 Canonical Raw 存储、Routing QoS 与信号处理。本次只完成 #64 的生命周期/组合根接线，使这些后续组件统一挂到一个 Session Authority 下，不再各自维护 Start/Stop 状态。

## 运行

```powershell
dotnet run --project samples/UpperHost.Sample.DataAcquisition/UpperHost.Sample.DataAcquisition.csproj
```

Simulator 生成 500 帧、4 通道数据。程序最终输出 Session 终态、生产帧数、Consumer 统计和存储文件路径。
