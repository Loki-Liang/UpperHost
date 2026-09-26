Issue: #59
Reason: Add the reusable block-oriented Signal Processing Runtime required by the Acquisition route without coupling Processing to WPF, FileSystem, transports, or a specific DSP library.
Surface: OpenDeviceStudio.SignalProcessing adds immutable SignalBlock/descriptor/lineage contracts, validated/frozen Stage Graph compilation, stable configuration/plan hashing, per-session/partition stage lifecycle, gap/reset policies, bounded graph execution through OpenDeviceStudio.Dataflow StreamRouter, runtime snapshots and low-cardinality observability.
