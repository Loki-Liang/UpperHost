# Automation Runtime

[English](automation-runtime.md) | 简体中文

UpperHost 的工站、测试台和多设备序列只有一条权威执行链：

```text
产品 UI / Application Service
        |
AutomationExecutionCoordinator
        |
已编译 AutomationExecutionPlan + 冻结 AutomationRecipeSnapshot
        |
Step 完整资源声明
        |
与 #60 共用 ICommandResourceArbiter
        |
AutomationStepContext.DispatchAsync
        |
Host-owned BoundedCommandDispatcher<TCommand,TResult>
        |
CommandRuntime -> Guard / Interlock / Completion / UnknownOutcome
        |
Device Capability -> Protocol -> Transport -> Hardware
```

`WorkflowRunner` 仅保留为兼容/简单流程 Facade；生产 Automation 禁止再建立第二套 Station Scheduler、Device Queue 或资源协调器。

## ExecutionId 与不可变输入

每次运行都有唯一 `ExecutionId`。执行前必须先编译 Plan，冻结 Workflow id/version、NodeId、资源、Retry 合同和 Required Capability。

`AutomationRecipeSnapshot.Create` 会把已经解析完成的 Recipe Canonical JSON 固化，并计算 SHA-256 Hash；运行时不保存调用方可变 Recipe 对象引用。

Step Output 使用 `AutomationDataKey<T>`。Parallel 分支各自持有 branch-local Data，Join 后才确定性 Merge；同 Key 冲突直接失败，禁止多个并发 Step 共享一个可变 Dictionary。

## Station / Execution 双层状态

`AutomationExecutionCoordinator` 是 Station Authority，同一 Station 同时只能有一个 active execution。

Execution State 用于表达 `PauseRequested/StopRequested/AbortRequested/RecoveryRequired/Stopped/Aborted` 等执行语义；Station State 表达 `Idle/Preparing/Running/Paused/Stopping/Faulted/Recovering/Completed` 物理/业务生命周期。

Station transition 锁内禁止执行设备 I/O、Journal I/O 或业务回调。

## Pause、Stop、Abort、Emergency Stop

- **Pause**：协作式请求，只在 `SafePause` Checkpoint 或 `PauseBoundaryAfter=true` 的安全边界进入 Paused。
- **Stop**：有序停止；观察到 Stop 后不再启动新 Node。正在运行的 Step 只有显式 `CancelOnStop=true` 才会被取消。
- **Abort**：立即向整个 owned execution tree 传播取消；设备 Step 仍必须保留 #60 的真实完成语义，越过副作用边界后取消可能变成 `UnknownPhysicalOutcome`。
- **Emergency Stop**：软件 Runtime 不伪装硬急停。硬件急停、安全继电器、安全 PLC 仍是独立安全链，软件只观察其状态/故障。

## 资源所有权与 #60 共用仲裁

Step 必须通过 `CommandResourceClaim` 一次声明完整物理资源集合。#60 的共享 `ICommandResourceArbiter` 会去重并按稳定顺序获取整组 Lease。

持有 Lease 后，设备命令必须通过 `AutomationStepContext.DispatchAsync`。Context 会校验命令资源是否已在 Step 预声明，并在进入 Dispatcher 时去掉已经持有的 Claim，因此不会出现 Automation 先锁设备、Dispatcher 再锁同一设备造成 self-deadlock。

Manual Command 仍走同一个 Arbiter，因此 Manual 与 Automation 的资源冲突由同一权威路径处理。禁止创建 Automation 专用 Device Lock 或第二个私有 Dispatcher。

## Parallel / Join

`AutomationParallelNode` 必须显式给出 `MaxConcurrency` 和 JoinMode：

- `WaitAll`：等待并观察全部 owned child。
- `FailFast`：首个失败后请求取消其他 child，但返回前仍观察全部 child。

禁止 fire-and-forget。任一分支出现 Unknown Physical Outcome 时，其安全优先级高于普通 sibling failure，不能被 FailFast 掩盖。

## Retry / Compensation

Retry 默认关闭，只有 `AutomationRetryPolicy` 显式允许的已知失败类型才能重试。编译器会拒绝把 `UnknownPhysicalOutcome`、`RecoveryRequired`、Cancel 或 Stop 加入自动重试集合。

Compensation 是显式业务动作，不是异常回滚。主动作失败且 Compensation 也失败时，Execution 必须进入 `RecoveryRequired`。

## Recovery 与 #63 Reconcile

Journal 只是证据，不是物理真相。恢复前必须经过 `IAutomationRecoveryReconciler`。

`DeviceControlStateRecoveryReconciler` 对接 #63：产品 Rehydrate 回调负责重连、Readback，并把最终状态写入 `DeviceControlStateRegistry`；只有 Device 已回到 `Ready` 才算 Reconciled。

规则：

- `UnknownPhysicalOutcome` 默认绝不自动 Resume，即使后续 Readback 成功也进入 ManualIntervention。
- 已知结果只有在权威 Reconcile 成功且存在 `SafeRecovery` Checkpoint 时，才能得到 `ResumeSafeCheckpoint` 决策。
- Reconcile 成功但没有安全 Checkpoint 时只能 `Restart`。
- Reconcile 失败/不完整必须 ManualIntervention。

Coordinator 只返回 Recovery Decision，不会根据 Journal 偷偷重放历史命令。

## Execution Journal

Source Scaffold 默认由 Host 持有 `BoundedAutomationExecutionJournal`：有界容量、单 Writer、每个 Execution 单调 Sequence，并显式支持 `FailExecution` / `DegradeAndAlert`。

Journal 写入发生在 Device Resource Lease 释放之后，因此慢磁盘/数据库不会变成设备 I/O 锁持有点。

产品可在 Composition Root 替换 `IAutomationJournalSink` 为 SQLite、数据库或文件持久化实现。

## Composition Root

`AddUpperHostApplication()` 默认安装 Automation Runtime。需要显式组合时：

```csharp
builder.AddAutomationRuntime();
builder.AddCommandDispatcher<AxisCommand, AxisResult>();
```

产品在 Build Host 前注册 `ICommandable<,>`、Guard/Interlock 以及产品自己的 `IAutomationRecoveryReconciler`。

可运行参考：`samples/UpperHost.Sample.AutomationStation`，真实走 Frozen Recipe、共享资源仲裁、Bounded Dispatcher、Safe Checkpoint、Parallel Join 与 Interlock Reject。
