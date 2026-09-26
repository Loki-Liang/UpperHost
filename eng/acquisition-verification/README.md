# Acquisition verification profiles

Issue #62 uses versioned capacity profiles rather than scattered performance constants.

The profile contract intentionally separates three source modes:

- `VirtualTimeDeterministic` for correctness and sequence-driven fault tests.
- `WallClockPaced` for backlog, render-lag, and real-time behavior.
- `MaxThroughput` for saturation and throughput measurement.

`expectedRawBytesPerSecond` is a machine-checked invariant:

`sourceCount * channelsPerSource * bytesPerSample * sampleRateHz`.

Profiles are evidence inputs, not performance claims. Synthetic evidence must not be reported as real-hardware validation. Required branches may never select a lossy overflow policy. Optional presentation branches may drop according to their declared policy.

The validator is `scripts/validate_acquisition_verification.py`. It rejects unknown fields, invalid numeric-type widths, duplicate branches, unsafe QoS combinations, inconsistent expected data rates, missing required profiles, and missing slow-disk / slow-processing / slow-presentation campaign coverage.
