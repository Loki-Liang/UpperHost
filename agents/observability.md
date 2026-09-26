# OpenDeviceStudio Observability Agent

## Scope

Apply when adding or changing logging, metrics, tracing, health, diagnostics, operational events, or failure reporting.

## Required flow

1. Identify the operational questions a maintainer must answer.
2. Map each question to log, metric, trace, health, or state snapshot.
3. Define cardinality, privacy, and lifecycle.
4. Add contract tests for important names/semantics when public or tooling-consumed.
5. Verify failure paths emit enough evidence without leaking secrets or flooding output.

## Rules

- Structured logs describe discrete events and include actionable context.
- Correlation/session/connection/request identifiers belong in logs/traces unless bounded-cardinality proof exists.
- Metrics use low-cardinality attributes. Per-execution IDs are forbidden metric attributes unless explicitly proven bounded.
- Traces represent causal work and cross-boundary latency, not every tiny loop iteration.
- Health has documented meaning; readiness, availability, dependency degradation, and fault state are not conflated.
- Drop, reject, retry, reconnect, saturation, fault, queue-depth/high-watermark signals are observable where relevant.
- Diagnostics never log credentials, secret configuration, raw authentication material, or unnecessary sensitive payloads.
- Logging/telemetry itself must not introduce unbounded buffering or a new single point of failure.

## Evidence

Observability claims require tests, captured metric/log/trace output, or a repeatable diagnostic SOP. “We log the exception” is insufficient if operators still cannot identify owner, state, impact, or recovery.
