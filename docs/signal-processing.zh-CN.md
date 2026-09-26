# Signal Processing Runtime

[English](signal-processing.md)

OpenDeviceStudio.SignalProcessing 是 Canonical RawData 被 Required Raw Recorder 接受之后使用的 block-oriented 处理 Runtime。它不解析 Transport bytes、不拥有 Raw 持久化，也不依赖 WPF。

## 边界

```text
Transport / Protocol decoder
  -> CanonicalRawBlock
  -> Required Raw recorder Accepted
  -> 产品 Raw-to-Signal projection
  -> SignalProcessingRuntime<T>
       -> validated/frozen Stage Graph
       -> StreamRouter bounded edges
       -> Required conditioning / algorithms
       -> Optional algorithms / presentation taps
```

Processing Plan 在 Session 热路径开始前编译。Compile 会校验 StageId 唯一、InputStage 可解析、Graph 无环、Descriptor 兼容，以及 QoS/continuity 规则，然后冻结 descriptor 并生成稳定 PlanHash。

## Stage 生命周期

Stage Factory 描述稳定契约：StageId、Version、稳定 ConfigHash、输入/输出 Descriptor 变换、stateful、continuity/order 要求、GapPolicy、algorithmic delay，以及可选 Window contract。

Runtime 按 SignalPartitionKey（SourceId + ChannelLayoutId）创建独立 stage instance。不同设备/partition 不共享有状态滤波历史。热路径不查 DI、不反射解析 Graph，也不按 sample 创建 Task。

## 有界 Graph 执行

每条 Graph edge 都实际复用 OpenDeviceStudio.Dataflow.StreamRouter：

- Required edge 使用 bounded lossless Wait/Reject，并传播 fault；
- Optional edge 可使用显式 lossy policy，并隔离 fault；
- continuity-required stage 禁止接到 lossy edge；
- Optional/lossy ancestor 下不能再伪装声明全局 Required child。

因此 slow optional algorithm / presentation 不会无声拖死 Required processing path。

## Gap Policy

continuity-required stage 必须显式选择：

- Fault：终止 Required processing path；
- ResetAndMarkQuality：reset stage state，以当前 block 重建 continuity，并标记 quality；
- DropUntilReinitialized：阻塞该 partition，直到 ResetPartitionAsync 建立新的状态边界。

禁止检测到 gap 后继续沿用旧 filter/window history，却仍输出伪 Good 结果。

## Lineage 与 Replay

每个 derived SignalBlock 都携带 Source/Session、ProcessingEpoch、input sequence/time range、stage id/version/config hash、output descriptor 和声明的 algorithmic delay。Online 与 Replay 使用同一个 CompiledSignalProcessingPlan / Runtime，不存在第二套“历史算法”。

## DSP Backend

Core public contract 不泄漏 NWaves 或其他第三方 DSP 类型。OpenDeviceStudio.SignalProcessing.NWaves 当前提供一个基于 NWaves 的 stateful online moving-average reference stage。产品可增加自己的已验证 adapter，而无需改 Core Graph/Runtime API。

## 不属于 Core 的职责

- Raw byte framing / protocol parsing
- Canonical Raw persistence
- 产品 calibration 语义
- 医疗/计量算法验证
- WPF render/downsample
- 不可信算法代码的进程级隔离
