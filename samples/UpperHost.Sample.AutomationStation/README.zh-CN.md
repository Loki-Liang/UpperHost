# UpperHost 自动化工站示例

[English](README.md) | 简体中文

这个 Console 示例专门验证 UpperHost 的 **Automation 自动化路线**。Protocol/Transport 字节通信已经由 Device Control Sample 展示，本例聚焦多设备编排。

## 展示能力

- 3 台注册设备：安全门、X 轴、检测相机。
- 轴命令通过 `CommandRuntime` 执行。
- `IInterlock<AxisCommand>`：安全门打开时禁止轴运动。
- `StateMachine<StationState,StationTrigger>` 作为工站生命周期权威。
- `WorkflowRunner` 执行 `回零 -> 移动 -> 拍照 -> 判定`。
- 同时演示一次成功流程和一次确定性的联锁拒绝流程。

```text
工站 State Machine
        |
     Workflow
        |
+-------+--------+
| Axis Runtime   | Camera
|   -> Interlock |
+-------+--------+
        |
      Devices
```

.NET 10 下可直接运行：

```powershell
dotnet run --project samples/UpperHost.Sample.AutomationStation/UpperHost.Sample.AutomationStation.csproj
```

第一次安全门关闭，流程成功；随后程序 Reset、打开安全门再次运行，第一条轴命令会在到达 Axis Device 之前被 Interlock 拒绝，工站进入 `Faulted`。

这里的软件联锁只用于应用层控制约束，不能替代硬件急停、安全继电器、安全 PLC 或认证安全回路。
