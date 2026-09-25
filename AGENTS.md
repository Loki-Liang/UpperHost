# UpperHost AI Development Governance

[简体中文](AGENTS.zh-CN.md) | English

This file is the repository-level authority for AI-assisted development. Tool-specific instructions may add detail but must not weaken or bypass these rules.

## Default development executor

For normal feature, fix, refactor, test, documentation, and architecture implementation work, **OpenHands is the default implementation executor for UpperHost**.

Other assistants or orchestration tools may inspect issues, plan work, review diffs, inspect CI, and coordinate delivery. Production implementation should be handed to OpenHands unless one of these exceptions applies:

- OpenHands is unavailable or cannot access the repository.
- The task is repairing the OpenHands integration/governance itself.
- A repository-administration action cannot be performed from OpenHands.

When a fallback executor is used, it must follow the same workflow and validation rules and record the reason in the PR.

## Required delivery flow

```text
Current main + Issue/Task
        |
        v
OpenHands implementation
        |
        v
Production code + matching tests/docs
        |
        v
Focused validation
        |
        v
.openhands/pre-commit.sh
        |
        v
Pull Request
        |
        v
GitHub Actions Windows gate
        |
        v
Diff / architecture / test review
        |
        v
Squash merge to main
```

## Current-facts rule

Before each implementation or repair cycle, re-read the current facts from GitHub:

- latest `main` SHA;
- issue acceptance criteria and dependencies;
- PR base/head/exact-head SHA when a PR exists;
- current diff and review threads;
- current CI run/job/step state and the first actionable failure.

Do not continue from stale CI conclusions or an old branch assumption.

## Branch and merge rules

1. Never implement normal work directly on `main`.
2. Create a short-lived branch from the latest `main`.
3. Keep one coherent delivery scope per branch/PR.
4. Production implementation, tests, and required documentation move together.
5. Merge only after the exact PR head has passed required CI and unresolved review threads are cleared.
6. Prefer squash merge to keep `main` history delivery-oriented.
7. Do not merge by assumption; verify the resulting commit is actually on `main`.

## Failure handling

1. Fix the first actionable failure.
2. After a fix, continue validation from the failed point when the tooling supports it.
3. Do not repeatedly restart already-passing expensive validation.
4. Run the complete required gate once actionable failures are closed.
5. Do not use reruns to hide deterministic production, test, script, workflow, or environment-contract defects.

## Test and documentation rules

- Production behavior changes require risk-matched automated tests.
- Prefer focused tests for the affected module before repository-wide validation.
- Keep English/Chinese documentation pairs synchronized where both exist.
- Update architecture or extension documentation when public boundaries change.
- Do not mark a task complete when only implementation exists but tests/docs required by the acceptance criteria are missing.

## Architecture invariants

1. Core remains industry-neutral.
2. Device behavior uses capability composition, not a giant inheritance hierarchy.
3. Transport handles transfer mechanics; Protocol owns framing and semantic decoding.
4. Ordered bytes use `ITransport`; discrete messages/frames use `IMessageTransport<TMessage>`; vendor SDK domain APIs map directly to Device Capabilities.
5. Request/response, message/frame, and streaming remain explicit boundaries.
6. Vendor/native dependencies stay in provider packages, never `UpperHost.Abstractions`.
7. UI and Workflow call application/device capabilities instead of sockets or native SDKs directly.
8. Backpressure and loss policy are explicit for streaming.
9. WPF is an adapter, not Core.
10. Software guards/interlocks never claim to replace certified hardware safety mechanisms.

## Validation authority

OpenHands usually runs in a Linux sandbox and must use `.openhands/setup.sh` and `.openhands/pre-commit.sh` for fast pre-PR validation.

The authoritative release/integration gate remains GitHub Actions on Windows because UpperHost contains WPF/Windows targets. Linux-only success must never be reported as Windows runtime/UI validation.

## Secrets

Never commit OpenHands credentials, model API keys, PATs, signing material, device secrets, or machine-local configuration.
