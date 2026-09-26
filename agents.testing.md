# UpperHost Testing and Evidence Agent

## Scope

Apply to every production behavior change and every bug fix.

## Required flow

1. Convert Issue acceptance criteria into an Evidence Matrix before implementation.
2. Select the smallest test layer that can prove each behavior without mocking away the risk.
3. Add regression coverage for every repaired deterministic defect.
4. Add fault/contract/performance coverage when the risk exists.
5. Run focused tests first, then repository pre-commit, then authoritative CI.
6. Keep exact test/evidence references in the PR or Issue.

## Evidence Matrix

Every acceptance criterion must map to at least one proof.

| Acceptance criterion | Primary evidence | Secondary evidence if needed |
| --- | --- | --- |
| pure deterministic logic | Unit Test | property/boundary cases |
| module behavior | Component Test | state snapshot |
| provider/module integration | Integration Test | simulator |
| API/protocol/config contract | Contract Test | compatibility guard |
| disconnect/timeout/queue full | Fault Injection | log/metric assertion |
| latency/throughput/allocation | Benchmark/Load Test | profiler evidence |
| long-run leak/backlog risk | Soak Test | resource metrics |
| release/platform behavior | CI/platform test | repeatable SOP |

An acceptance item without evidence is incomplete design, not future test work.

## Test layers

- **Unit:** pure logic, parsers, policies, state transitions, validation.
- **Component:** one real module with its actual lifecycle/concurrency behavior.
- **Integration:** real repository modules/providers wired together.
- **Contract:** public API, protocol, serialization, configuration, compatibility surfaces.
- **Fault injection:** active disconnect, timeout, partial data, duplicate/reorder, saturation, dependency/storage failure, restart.
- **Performance/load/soak:** hot paths, bounded allocation, backlog behavior, long-running stability.
- **Regression:** reproduces an actual defect.
- **Platform:** Windows/WPF/native behavior on the authoritative platform.

Do not use a mocked happy path to claim a real transport, concurrency, persistence, or recovery risk is tested.

## Quantified acceptance

Rewrite vague claims into measurable behavior. Example “supports reconnect” should specify detection trigger/timeout, reconnect state/backoff, stop/cancellation, duplicate-worker prevention, data/sequence semantics, metric/log evidence, fault test, and repeated-cycle/leak evidence when warranted.

The actual numbers belong in the Issue, not in this global rule.

## Required method skills

- .openhands/skills/fault-injection.md for failure/recovery behavior.
- .openhands/skills/contract-attack.md for public/stateful APIs.
- .openhands/skills/capacity-backpressure-audit.md for saturation/throughput behavior.

## Failure policy

Do not rerun a deterministic failure until it passes by chance. Identify and fix the root cause, then rerun from the failed point where tooling allows it.
