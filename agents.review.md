# UpperHost Review Agent

## Scope

Every production behavior change must pass this review gate. Medium/high-risk changes run all five reviews in order. For truly documentation-only or mechanical changes, skipped reviews must be explicitly marked NotApplicable with a reason.

Review is not a report. Every blocker found must be fixed in code, tests, documentation, Issue acceptance criteria, or architecture, then the affected review is repeated.

## Required flow

1. Freeze the exact diff/head being reviewed.
2. Re-read applicable rules, acceptance criteria, and Evidence Matrix.
3. Run Review 1 through Review 5.
4. Record findings as Blocker / Required / Advisory.
5. Fix every Blocker and Required finding that is in scope.
6. Re-run affected focused tests and review rounds.
7. Only then proceed to full required validation.

## Review 1 — Architecture attack

Assume the structure is wrong.

Attack responsibility/module boundaries, data/state ownership, dependency direction, invariants, lifecycle, capacity/backpressure, compatibility, change axis, and unnecessary framework/abstraction complexity.

Use .openhands/skills/adversarial-review.md and, when applicable, .openhands/skills/invariant-analysis.md.

## Review 2 — Failure attack

Assume the system is under partial failure.

Attack disconnect, timeout, hot-unplug, dependency failure, process restart, queue saturation, disk/storage failure, resource pressure, duplicate/reordered/partial data, consumer stall/fault, retry/reconnect exhaustion, and shutdown while work is in flight.

Use .openhands/skills/premortem.md and .openhands/skills/fault-injection.md.

## Review 3 — Implementation attack

Attack races/shared state, async/cancellation, resource leaks, double lifecycle calls, error propagation, idempotency, hidden state, unbounded work, and over-abstraction.

Use agents.implementation.md and .openhands/skills/contract-attack.md.

## Review 4 — Acceptance/Evidence attack

Ask: **what proves this requirement is complete?**

Every acceptance criterion must point to an observable artifact: automated test, fault-injection result, benchmark/soak evidence, log/metric/trace assertion, static guard, CI gate, or repeatable SOP when automation is not technically feasible.

“Works”, “supports reconnect”, “performance is good”, and “tests pass” are not acceptance evidence.

Use agents.testing.md.

## Review 5 — Maintainer attack

Assume a new maintainer receives a production alert two years from now with no current-chat context.

Check whether they can identify owner/lifecycle, locate the failure from diagnostics, determine data-loss/duplicate impact, safely change configuration/behavior, upgrade/rollback, and understand assumptions from code/tests/docs.

A design that only the current author can safely operate is not complete.

## Exit criteria

- zero unresolved Blocker/Required findings;
- Evidence Matrix complete;
- focused validation rerun after fixes;
- exact reviewed head is the head sent to required CI.
