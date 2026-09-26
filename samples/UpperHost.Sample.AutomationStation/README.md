# UpperHost Automation Station Sample

English | [简体中文](README.zh-CN.md)

This console sample exercises the production Automation route rather than a demo-only runner.

## Authoritative path

```text
AutomationExecutionCoordinator
  -> compiled plan + frozen recipe snapshot
  -> shared ICommandResourceArbiter
  -> AutomationStepContext.DispatchAsync
  -> BoundedCommandDispatcher
  -> CommandRuntime / DoorClosedInterlock
  -> Axis / Camera devices
```

The sample includes:

- one Host-owned station/execution authority;
- immutable recipe values and recipe hash;
- exclusive axis/camera resource claims shared with manual command dispatch;
- `home -> SafeRecovery checkpoint -> move -> SafePause checkpoint`;
- bounded parallel inspection branches and deterministic join;
- typed branch output merged before judging;
- #60 command/interlock/UnknownOutcome mapping through `AutomationStepResult.FromCommand`;
- graceful Host shutdown of the journal and command dispatchers.

Run:

```powershell
dotnet run --project samples/UpperHost.Sample.AutomationStation/UpperHost.Sample.AutomationStation.csproj
```

Cycle 1 runs with the safety door closed and should complete. The station is reset, the door is opened, and Cycle 2 is rejected by the existing #60 software interlock before the axis device performs motion.

Pause/Stop/Abort, UnknownOutcome recovery, shared Manual/Automation contention and journal ordering are covered by `AutomationRuntimeTests` so those failure paths do not depend on timing a console demo.

The software interlock does not replace a hardware emergency stop, safety relay or safety PLC.
