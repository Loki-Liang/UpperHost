# Signal Processing runtime

[简体中文](signal-processing.zh-CN.md)

OpenDeviceStudio.SignalProcessing is the block-oriented processing runtime used after Canonical RawData has been accepted by the Required Raw recorder. It does not parse transport bytes, own Raw persistence, or depend on WPF.

## Boundary

```text
Transport / Protocol decoder
  -> CanonicalRawBlock
  -> Required Raw recorder Accepted
  -> product Raw-to-Signal projection
  -> SignalProcessingRuntime<T>
       -> validated/frozen Stage Graph
       -> StreamRouter bounded edges
       -> Required conditioning / algorithms
       -> Optional algorithms / presentation taps
```

A Processing Plan is compiled before the session hot path starts. Compilation validates unique StageId values, resolved inputs, acyclic topology, descriptor compatibility and QoS/continuity rules, then freezes descriptors and produces a stable PlanHash.

## Stage lifecycle

A stage factory describes one stable stage contract: StageId, version, stable configuration hash, input/output descriptor transformation, statefulness, continuity/order requirements, gap policy, algorithmic delay and optional window contract.

Runtime stage instances are created per SignalPartitionKey (SourceId + ChannelLayoutId). Stateful filter history is therefore not shared across devices/partitions. Hot-path processing never resolves DI, reflects over the graph, or creates a Task per sample.

## Bounded graph execution

Every graph edge is implemented with OpenDeviceStudio.Dataflow.StreamRouter.

- Required edges use bounded lossless Wait/Reject semantics and propagate faults.
- Optional edges may use explicit lossy policies and isolate faults.
- A continuity-required stage cannot be attached through a lossy edge.
- A Required child cannot be declared below an Optional/lossy ancestor.

This keeps a slow optional algorithm or presentation tap from silently stalling the Required processing path.

## Gap policy

Continuity-required stages choose one explicit policy:

- Fault: terminate the Required processing path.
- ResetAndMarkQuality: reset stage state, rebase continuity at the current block, and mark output quality.
- DropUntilReinitialized: block the affected partition until ResetPartitionAsync establishes a new state boundary.

No policy silently continues old filter/window history across a detected discontinuity.

## Lineage and replay

Every derived SignalBlock records Source/Session, ProcessingEpoch, input sequence/time range, stage id/version/config hash, output descriptor and declared algorithmic delay. Online and replay use the same CompiledSignalProcessingPlan and runtime; replay is not a second historical-algorithm implementation.

## DSP backend seam

Core contracts contain no NWaves or other third-party DSP types. OpenDeviceStudio.SignalProcessing.NWaves currently provides a reference stateful online moving-average stage backed by NWaves. Products can add other validated stage adapters without changing the graph/runtime public contract.

## What remains outside Core

- Raw byte framing and protocol parsing
- Canonical Raw persistence
- product-specific calibration semantics
- medical/measurement algorithm validation
- WPF rendering/downsampling
- process isolation for untrusted algorithm code
