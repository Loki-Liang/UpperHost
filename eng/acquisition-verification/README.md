# Acquisition verification profiles

Issue #62 uses versioned capacity profiles rather than scattered performance constants.

## Authoritative data path

Profiles preserve the repository's Raw-first invariant:

```text
Synthetic/Provider
      |
      v
Canonical RawBlock
      |
      +----> Raw Recorder (Required, independent bounded queue)
      |
      v
Stream Router
      +----> Processing (Required)
      +----> Presentation / optional algorithms (Optional, explicitly lossy when configured)
```

Raw persistence is **not** modeled as a Router branch. `rawRecorder` owns its own queue and durability policy; `routerBranches` describe only downstream fan-out after Raw acceptance.

The profile contract separates three source modes:

- `VirtualTimeDeterministic` for correctness and sequence-driven fault tests.
- `WallClockPaced` for backlog, render-lag, and real-time behavior.
- `MaxThroughput` for saturation and throughput measurement.

`expectedRawBytesPerSecond` is a machine-checked invariant:

`sourceCount * channelsPerSource * bytesPerSample * sampleRateHz`.

Profiles are evidence inputs, not performance claims. Synthetic evidence must never be reported as real-hardware validation. Required Router branches may not select a lossy overflow policy. Optional presentation branches may not use `Wait`, so UI work cannot backpressure the required path.

The validator is `scripts/validate_acquisition_verification.py`. It rejects unknown fields, invalid numeric-type widths, duplicate branches, unsafe QoS combinations, inconsistent expected data rates, missing required profiles, and missing slow-disk / slow-processing / slow-presentation campaign coverage.
