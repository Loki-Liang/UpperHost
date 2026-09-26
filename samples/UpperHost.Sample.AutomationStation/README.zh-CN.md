# UpperHost 自动化工站示例

[English](README.md) | 简体中文

这个 Console Sample 真实走生产 Automation Runtime，不再维护一套 Sample-only Workflow/State 权威。

## 权威链路

```text
AutomationExecutionCoordinator
  -> Compiled Plan + Frozen Recipe Snapshot
  -> 与 Manual 共用 ICommandResourceArbiter
  -> AutomationStepContext.DispatchAsync
  -> BoundedCommandDispatcher
  -> CommandRuntime / DoorClosedInterlock
  -> Axis / Camera Device
```

示例覆盖：

- Host-owned 单 Station / Execution Authority；
- Recipe 已解析值冻结与 Recipe Hash；
- Axis/Camera Exclusive Resource Claim，与 Manual Command 共用仲裁；
- `回零 -> SafeRecovery Checkpoint -> 移动 -> SafePause Checkpoint`；
- bounded Parallel 检测分支与确定性 Join；
- 分支 Typed Output 在 Join 后 Merge，再进行质量判定；
- 通过 `AutomationStepResult.FromCommand` 保留 #60 Interlock / Completion / UnknownOutcome 语义；
- Host 停止时 Journal 与 Command Dispatcher 确定性退出。

运行：

```powershell
dotnet run --project samples/UpperHost.Sample.AutomationStation/UpperHost.Sample.AutomationStation.csproj
```

Cycle 1 在安全门关闭时完成；随后 Reset，打开安全门执行 Cycle 2，轴动作会在到达 Axis Device 前被 #60 软件联锁拒绝。

Pause/Stop/Abort、UnknownOutcome Recovery、Manual/Automation 资源竞争以及 Journal 顺序由 `AutomationRuntimeTests` 做确定性故障测试，不依赖 Console Demo 的人工时序。

软件联锁不能替代硬件急停、安全继电器或安全 PLC。
