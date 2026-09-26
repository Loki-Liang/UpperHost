# UpperHost Acquisition Agent

## Scope

Apply to DAQ, EMG, sensor streams, raw capture, parsing, processing/filtering, algorithms, stream routing/fan-out, downsampling, display feeds, persistence, export, and replay.

## Canonical data model

Keep these meanings explicit:

- **Wire/Transport Data:** provider bytes/messages before acquisition normalization.
- **Raw Data:** the first lossless acquisition record after only the framing/decoding needed to establish record boundaries, source, sequence, timestamp, and session identity; no algorithmic filtering or derived computation.
- **Parsed Data:** typed/domain fields decoded from raw records when parsing is a distinct stage.
- **Processed Data:** filtered, calibrated, resampled, normalized, or otherwise transformed data.
- **Derived Data:** features/results such as RMS, FFT features, events, classifications, or aggregates.
- **Display Data:** visualization-oriented projection/downsample; never the authoritative record.

If the source format makes Raw and Parsed the same immutable record, document that explicitly instead of manufacturing a fake stage.

## Required flow

1. Draw the complete acquisition path before coding.
2. Use the Dataflow Trace skill on one real datum from device to persistence/processing/display/export.
3. Define ownership, timestamps, sequence/gap semantics, queue capacity, overflow, failure, cancellation, and replay at each stage.
4. Classify branches as Required or Optional.
5. Define shutdown/fault behavior and which evidence proves no silent loss.
6. Add focused, fault, and saturation tests.

## Raw persistence rule

For acquisition routes whose product policy requires/defaults to raw recording, raw persistence is a first-class **Required** path and must not depend on optional algorithm, UI, export, or lossy fan-out consumers.

Persist canonical Raw Data as early as practical after record boundary/metadata normalization. Do not wait for downstream filtering, feature computation, display downsampling, or optional fan-out to succeed before making raw data durable.

If byte-for-byte wire capture is also required, model that separately as Wire Capture; do not redefine processed samples as raw.

Raw persistence still needs explicit bounded buffering, storage failure semantics, session metadata, sequence/gap evidence, and deterministic stop/flush behavior. “Write to disk immediately” does not permit unbounded memory, blocking transport forever, or hiding disk failure.

## Fan-out and backpressure

- Required consumers represent delivery that the session cannot silently lose.
- Optional consumers such as UI previews may use an explicit lossy policy if product semantics allow it.
- One slow Optional branch must not silently stall a Required raw-persistence path.
- A Required branch rejecting/faulting produces explicit session/publish failure evidence; partial required delivery cannot be reported as success.
- Ownership/copy/retain-release semantics are correct for every branch.
- Buffer capacity, high-watermark, drop/reject/fault counters, and queue latency are observable where meaningful.

## Time, sequence, and session semantics

Every persisted/processed acquisition record has enough context to reason about session identity, source/channel identity, capture timestamp/time source, sequence/gap identity, ordering, duplicates, missing data, reconnect/reset boundary, and calibration/config version when interpretation depends on it.

Do not derive authoritative sample time from UI render cadence.

## Required method skills

- .openhands/skills/dataflow-trace.md
- .openhands/skills/capacity-backpressure-audit.md
- .openhands/skills/fault-injection.md
- .openhands/skills/invariant-analysis.md

## Minimum evidence

Depending on scope, prove raw persistence is independent from optional UI/algorithm slowdown, sequence/gap and partial-delivery semantics are observable, bounded capacity survives slow consumers, stop/fault obeys drain/cancel contract, repeated sessions do not leak workers/buffers/subscriptions, and replay/export uses authoritative persisted data rather than display projections.
