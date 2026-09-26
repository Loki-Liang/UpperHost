# Skill: Capacity and Backpressure Audit

## Purpose

Make overload behavior deterministic.

## Method

Inventory every resource where work can accumulate: Channel/Queue/Buffer, transport/native receive buffers, batches, connection/thread/task pools, caches, persistence writer queues, and fan-out branches.

For each resource define:

1. capacity/bound;
2. producer-rate assumption;
3. consumer-rate assumption;
4. full behavior: wait/reject/drop/latest/fault;
5. which data can be lost;
6. whether producer blocks and for how long;
7. cancellation/timeout behavior;
8. recovery after saturation;
9. high-watermark/drop/reject/latency evidence;
10. stress/soak test intentionally reaching saturation.

An unbounded resource requires explicit proof that growth is impossible or externally bounded.
