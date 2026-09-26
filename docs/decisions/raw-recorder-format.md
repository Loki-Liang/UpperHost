# Raw recorder format decision

Issue: #58

The default FileSystem Canonical RawData adapter uses **Apache Arrow IPC Stream** for segmented Raw artifacts.

## Evidence

The deterministic format spike compares Arrow IPC Stream with a deliberately minimal proprietary binary prototype using 2,048 blocks × 4,096 payload bytes. It verifies exact payload round-trip and truncated-tail recoverability before reporting size, elapsed write time, and allocation deltas in CI.

The earlier authoritative run showed:
- Arrow retained complete batches before a truncated tail and preserved exact canonical bytes.
- The minimal binary prototype was materially faster/smaller in the microbenchmark, but would create and permanently maintain a proprietary on-disk contract.
- Arrow provides a standardized typed, cross-language format and mature readers, which is more valuable for the default extensible scaffold than the prototype's microbenchmark advantage.

The default adapter therefore selects Arrow IPC Stream, while keeping the Core recorder contract format-neutral so products may provide EDF/EDF+, Parquet, HDF5, vendor/native, or database adapters.

## Boundaries

- Arrow is an adapter implementation detail in `OpenDeviceStudio.Storage.FileSystem`; it is not exposed by `CanonicalRawBlock` or `IRawRecorder`.
- Raw remains the original numeric/byte representation after framing/minimal decode; calibration/filtering/downsampling are downstream.
- Manifest JSON is session metadata only; Raw hot-path payload records are not JSONL.
- This spike is not the #62 performance/soak baseline and does not authorize per-block fsync.
