# OpenDeviceStudio Transport Agent

## Scope

Apply to TCP, Serial, USB, BLE, stream/socket/native transport adapters, connection management, reconnect, and ordered-byte transfer mechanics.

Protocol framing/semantic decoding belongs to Protocol, not Transport, but Transport must expose enough transfer/lifecycle facts for Protocol to behave deterministically.

## Required flow

1. Read existing provider/transport contracts and connection state machine.
2. Define connection lifecycle and ownership.
3. Answer the Transport Eight Breaks.
4. Define timeout/cancellation/retry/reconnect semantics.
5. Define bounded buffering/backpressure and loss behavior.
6. Add fault-injection and repeated recovery evidence.
7. Run state-machine and contract audits.

## Transport Eight Breaks

Every transport design must answer:

1. **Connection breaks:** how is disconnect/half-open/hot-unplug detected and surfaced?
2. **Data continuity breaks:** what data can be lost in-flight and how is the gap represented?
3. **Frame boundary breaks:** how do partial reads and concatenated reads reach the protocol parser without corruption?
4. **Ordering breaks:** what ordering is guaranteed and where can reordering occur?
5. **ACK/response breaks:** for request/response semantics, what happens when response/ack is delayed, duplicated, lost, or arrives after timeout?
6. **Consumer breaks:** what happens when the downstream reader/parser/consumer stalls or faults?
7. **Process/runtime breaks:** what happens on cancellation, dispose, process restart, device reset, or native-handle failure?
8. **Recovery breaks:** after reconnect/reopen, what exact state resumes and what must be reinitialized, replayed, rejected, or re-synchronized?

“Automatic reconnect” without answers to all applicable items is not a complete transport design.

## Rules

- Receive APIs assume arbitrary chunking; a read boundary is not a protocol frame boundary.
- Transport does not invent protocol semantics. Protocol owns framing, validation, and semantic decoding.
- Connection state is explicit; avoid unrelated booleans that permit impossible combinations.
- Connect/read/write/reconnect operations support cancellation and bounded timeout where meaningful.
- Retry/reconnect uses bounded/backoff policy with observable attempt/failure state; no tight infinite loop.
- Only one authoritative receive loop and one authoritative connection lifecycle own a connection at a time.
- Reconnect must not leak sockets, streams, native handles, subscriptions, timers, or duplicate background tasks.
- Send/receive queues are bounded unless a documented capacity proof justifies otherwise.
- Overflow behavior is explicit: wait, reject, drop, latest-only, or fault. Silent loss is forbidden.
- Where sequence/duplicate/idempotency belongs above Transport, preserve enough ordering/session metadata for the upper layer to enforce it.
- Device identity/provider selection is deterministic where multiple devices/adapters enumerate simultaneously.

## Required method skills

- .openhands/skills/state-machine-audit.md
- .openhands/skills/capacity-backpressure-audit.md
- .openhands/skills/fault-injection.md
- .openhands/skills/contract-attack.md

## Minimum evidence

Risk-matched tests cover the applicable subset of partial/concatenated reads, disconnect during read/write, cancellation during connect/reconnect, repeated cycles, downstream saturation, provider/native failure, duplicate receive-loop prevention, deterministic dispose, and resource stability.
