# UpperHost Automation Station Sample

English | [简体中文](README.zh-CN.md)

This console sample demonstrates the **automation** path independently from protocol/transport details already covered by the Device Control sample.

## What it demonstrates

- Three registered devices: safety door, X axis and inspection camera.
- `CommandRuntime` for axis commands.
- `IInterlock<AxisCommand>` blocking motion when the safety door is open.
- `StateMachine<StationState,StationTrigger>` as station lifecycle truth.
- `WorkflowRunner` for `home -> move -> capture -> judge` sequencing.
- A successful cycle and a deterministic rejected cycle.

```text
Station state machine
        |
     Workflow
        |
+-------+--------+
| Axis runtime   | Camera
|   -> Interlock |
+-------+--------+
        |
      Devices
```

Run on any platform with .NET 10:

```powershell
dotnet run --project samples/UpperHost.Sample.AutomationStation/UpperHost.Sample.AutomationStation.csproj
```

The first cycle succeeds with the door closed. The sample then resets, opens the door and runs again; the first axis command is rejected before it reaches the axis device and the station ends in `Faulted`.

This interlock is a software application constraint and does not replace certified hardware safety mechanisms.
