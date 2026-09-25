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

## Product positioning: upper-computer development scaffold

UpperHost is an **enterprise-grade .NET upper-computer development scaffold for industrial device control, automation, and data acquisition**. The repository exists to provide reusable runtime modules, project conventions, provider seams, testing infrastructure, reference samples and a runnable source application scaffold for developers building product-specific industrial upper-computer applications.

Product-specific device semantics, protocol details, control/interlock policy, workflow and UI belong to the application created with the scaffold. Repository-level feature work must strengthen reusable Runtime, Provider, Starter Application, Sample, Testing or Presentation capabilities that serve the Control, Automation or Acquisition paths.

`UpperHost.Workflows` and `UpperHost.StateMachines` remain code/API runtime modules.

See `docs/scaffold.md`.

## Architecture baseline: modular monolith

UpperHost is a **modular monolith by default**: one deployable application/process composed from strongly bounded modules and adapters. Do not introduce microservices, remote RPC boundaries, duplicated service-owned models, or distributed consistency merely to separate code. A distributed boundary requires an explicit issue/ADR with operational justification.

### Module dependency rules

1. `UpperHost.Abstractions` is the stable dependency root and must not reference another repository project.
2. Platform modules such as Control, Protocols, Dataflow, Workflows, StateMachines, Events, Resilience, Diagnostics and Testing expose narrow public contracts and must not depend on Presentation, the product App, Samples or Tests.
3. `UpperHost.Transport.*` and storage/provider packages are infrastructure adapters. They depend inward on stable contracts and must not push vendor/native concepts into Core.
4. `UpperHost.Presentation.*` is an outer adapter. Production modules must never depend back on presentation projects.
5. `UpperHost.Starters` is a composition/convenience module. Other production modules must not depend back on it.
6. `app/`, `samples/` and `tests/` may compose production modules; production modules never reference them.
7. ProjectReference cycles are forbidden.
8. Cross-module behavior uses public capabilities/contracts/events. Do not reach into another module's internals, use reflection to bypass boundaries, or create hidden static coupling.
9. Keep public APIs minimal. Types are internal/private unless another module genuinely needs the contract.
10. New modules require a clear responsibility, ownership boundary, allowed dependencies and matching tests; do not create a project only to move files.

The executable project-reference guard is `scripts/validate_architecture.py` and is part of pre-commit/CI.

## Engineering rules

- **DI/composition:** construct dependencies at application/Hosting/Starters composition roots. Do not use mutable global singletons or service-locator calls in domain/platform logic.
- **Async/I/O:** I/O paths are async end-to-end, accept `CancellationToken` where cancellation is meaningful, avoid `.Result`/`.Wait()`, and do not create unmanaged fire-and-forget tasks.
- **Resources:** sockets, streams, native handles and subscriptions have explicit ownership and deterministic disposal.
- **Errors:** never swallow failures. Translate vendor/infrastructure errors at module boundaries while preserving actionable context; stateful components enter an explicit fault state when appropriate.
- **Configuration:** use typed options/configuration with fail-fast validation. No scattered magic environment-variable reads or machine-specific paths in platform code.
- **Observability:** use structured logs, health and metrics at meaningful boundaries; never log secrets or raw sensitive credentials.
- **Concurrency:** shared mutable state requires an explicit synchronization/ownership model. Queues/channels must be bounded unless an unbounded design is explicitly justified.
- **Dependencies:** adding a NuGet/native dependency requires a reason and correct module placement. Core abstractions stay free of vendor SDK dependencies.
- **Compatibility:** treat public contracts, configuration contracts and the starter application structure as versioned surfaces. Breaking changes require explicit migration/documentation and corresponding tests.
- **Code shape:** prefer cohesive small types and explicit responsibilities; reject God classes, utility dumping grounds, duplicated protocol logic and copy-pasted provider implementations.
- **Testing:** each module owns unit tests for its behavior; add integration/contract tests for module/provider boundaries and simulator/fault tests for hardware-dependent behavior.

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

## Enterprise infrastructure quality gate

Any issue or PR that calls a cross-cutting component "enterprise-grade infrastructure" must be reviewed as an operational contract, not only as a package-integration checklist. Before merge, explicitly review and test the relevant dimensions:

1. API and module boundary: stable contracts, dependency direction and extension seams.
2. Reliability: failure modes, retry/recovery behavior, deterministic shutdown and partial-failure semantics.
3. Performance: hot-path overhead, allocation, blocking I/O and bounded resource usage.
4. Backpressure/loss: bounded queues or buffers, overload behavior and surfaced drop/loss signals.
5. Security/privacy: secret handling, redaction, unsafe diagnostics and least data exposure.
6. Configuration/lifecycle: typed configuration, fail-fast validation, defaults, startup and disposal.
7. Observability semantics: low-cardinality metrics, standard trace/error semantics, health meaning and self-observability.
8. Extensibility: first-party and third-party providers use one composition seam instead of copying cross-cutting wrappers.
9. Verification: unit + boundary/integration + fault-path tests that prove the operational invariants, not merely object registration.
10. Documentation/compatibility: public behavior, defaults and migration impact are documented in synchronized language variants.

For metrics, per-execution identifiers such as command/session/connection/request IDs are forbidden as metric attributes unless an explicit bounded-cardinality proof is documented. Put correlation IDs in logs/traces instead. For logging or streaming, unbounded buffering is forbidden and overload/drop behavior must be observable.

## Compatibility gate

Public runtime APIs, configuration contracts and the canonical source scaffold are versioned surfaces.

- `build-test-scaffold` validates `eng/compatibility/source-scaffold-contract.json` and compares every current reusable NuGet package against the exact PR base commit with Microsoft's `Microsoft.DotNet.ApiCompat.Tool`.
- The baseline must come from a successful CI artifact for the exact base SHA. Never fall back to an older successful main artifact.
- Normal ApiCompat failures are breaking changes. They require an explicit issue, migration instructions, a version change, tests and a same-PR `eng/compatibility/breaking/<PackageId>.md` approval record.
- Strict baseline validation detects additive public API drift. A deliberate public addition requires a same-PR `eng/compatibility/api-additions/<PackageId>.md` approval record explaining the public surface and why it belongs in the contract.
- Never delete a baseline, disable the analyzer/tool, weaken the source-scaffold contract or skip the gate to make CI green.
- The pre-commit hook validates the source-scaffold contract locally; the exact-base package comparison remains an authoritative PR/Windows CI gate because it requires the validated base artifact.

See `docs/compatibility.md`.
## Validation authority

OpenHands usually runs in a Linux sandbox and must use `.openhands/setup.sh` and `.openhands/pre-commit.sh` for fast pre-PR validation.

The authoritative release/integration gate remains GitHub Actions on Windows because UpperHost contains WPF/Windows targets. Linux-only success must never be reported as Windows runtime/UI validation.

## Secrets

Never commit OpenHands credentials, model API keys, PATs, signing material, device secrets, or machine-local configuration.
