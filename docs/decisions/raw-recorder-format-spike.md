# Raw recorder format spike

Issue: #58

This spike compares Apache Arrow IPC Stream with a deliberately minimal proprietary binary prototype before choosing the default FileSystem Raw adapter.

## Decision inputs

The benchmark uses deterministic canonical raw blocks with opaque byte payloads. It verifies exact byte round-trip and truncated-tail recovery before printing size, elapsed write time and process allocation deltas.

Apache Arrow .NET 23.0.0 is evaluated because it provides a standardized typed IPC stream, asynchronous I/O and cross-language readers. The custom binary path is intentionally prototype-only and is not a proposed public format.

The CI log produced by the **Raw format decision spike** step is the evidence for the final selection. Production code must not be added until this spike passes and the result is reviewed.

## Non-goals

- This is not a #62 performance baseline.
- The microbenchmark does not define hardware throughput targets.
- It does not authorize per-block fsync or JSONL Raw storage.
