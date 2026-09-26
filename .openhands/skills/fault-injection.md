# Skill: Fault Injection

## Purpose

Prove failure and recovery behavior by actively breaking dependencies.

## Method

Select faults relevant to the change, including disconnect/hot-unplug, connect/read/write timeout, partial/concatenated chunks, duplicate/reordered messages, downstream stall/fault, queue full, persistence/storage failure, provider/native exception, cancellation during startup/recovery, and process/session restart.

For each fault define expected:

- state transition;
- loss/duplicate/gap semantics;
- retry/recovery;
- resource cleanup;
- log/metric/trace evidence;
- caller-visible result.

Tests should be deterministic where practical and repeat recovery enough times to expose duplicate workers or leaks. “Exception was thrown” is not complete recovery evidence.
