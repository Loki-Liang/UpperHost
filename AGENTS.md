# OpenDeviceStudio AI Development Governance

[简体中文](AGENTS.zh-CN.md) | English

This file is the repository-level authority for AI-assisted development. It is intentionally a thin constitution: it defines mandatory process, rule precedence, routing, and completion gates. Domain-specific rules live under the executor-neutral agents/ directory, and reusable OpenHands analysis methods live under .openhands/skills/.

## Authority and rule hierarchy

Apply rules in this order:

1. AGENTS.md — repository constitution and mandatory delivery flow.
2. Applicable agents/*.md files — long-lived, executor-neutral domain engineering rules.
3. Approved ADRs and architecture decisions — project-specific technical decisions.
4. Current Issue/PR acceptance criteria — delivery-specific scope and measurable requirements.
5. Tool-specific execution profiles such as .openhands/skills/repo.md — execution detail only.

A lower layer may make a rule stricter but must not weaken a higher layer. If two rules conflict, stop and resolve the conflict explicitly instead of silently choosing one.

## Default development executor

For normal feature, fix, refactor, test, documentation, and architecture implementation work, **OpenHands is the default implementation executor for OpenDeviceStudio**.

Other assistants or orchestration tools may inspect issues, plan work, review diffs, inspect CI, and coordinate delivery. Production implementation should be handed to OpenHands unless one of these exceptions applies:

- OpenHands is unavailable or cannot access the repository.
- The task is repairing the OpenHands integration or AI-governance system itself.
- A repository-administration action cannot be performed from OpenHands.

A fallback executor must follow the same rules and record the reason in the PR.

## Product positioning

OpenDeviceStudio is an **enterprise-grade .NET upper-computer development scaffold for industrial device control, automation, and data acquisition**.

It is source-first and modular-monolith by default. Repository work must strengthen reusable Runtime, Provider, Application Scaffold, Testing, Presentation, Control, Automation, or Acquisition capabilities rather than introduce product-specific shortcuts into shared modules. See docs/scaffold.md.

## Mandatory delivery flow

Every production task follows this sequence:

    CURRENT FACTS
      -> Applicable Rules
      -> Existing Architecture
      -> Acceptance + Evidence Plan
      -> Implementation
      -> Focused Validation
      -> Five-Gate Review
      -> Fix Review Findings
      -> Full Required Validation
      -> Pull Request
      -> Exact-Head Required CI
      -> Squash Merge
      -> Verify Commit on main

Do not jump directly from an Issue to code.

## CURRENT FACTS

Before each implementation or repair cycle, re-read:

- latest main SHA;
- current Issue acceptance criteria, dependencies, and delivery state;
- PR base/head/exact-head SHA when a PR exists;
- current diff, reviews, and unresolved review threads;
- current CI run/job/step state and the first actionable failure;
- relevant code, tests, ADRs, and documentation.

Do not continue from stale CI conclusions or an old branch assumption.

## Applicable rule routing

All production-code changes read agents/implementation.md, agents/testing.md, agents/review.md, and agents/git.md.

Read additional rules when the task touches the corresponding scope:

| Scope | Required rule |
| --- | --- |
| module boundaries, architecture, new framework, public boundary | agents/architecture.md |
| transport, TCP, Serial, USB, BLE, connection/reconnection | agents/transport.md |
| streaming acquisition, DAQ, EMG, raw data, processing, fan-out | agents/acquisition.md |
| logging, metrics, tracing, health | agents/observability.md |
| trust boundary, credentials, authorization, external input | agents/security.md |
| packaging, deployment, release readiness, rollback | agents/release.md |
| public API, configuration, source-scaffold compatibility | agents/compatibility.md |

The machine-readable routing contract is .github/governance/agent-governance.json.

## Global engineering constitution

- Production code must not be a Demo, PoC, tutorial-only, or happy-path-only implementation.
- Prefer existing platform capabilities. A new dependency, framework, abstraction, or distributed boundary requires an explicit technical reason and trade-off.
- Stateful behavior must have explicit ownership, lifecycle, cancellation, failure, and recovery semantics.
- Shared mutable state requires an explicit synchronization or ownership model.
- Queues, channels, buffers, retries, and other potentially growing resources must be bounded or explicitly justified.
- Errors must not be swallowed. Infrastructure/vendor errors are translated at boundaries while retaining actionable diagnostic context.
- Bug fixes require a regression test that can fail before the fix and pass after it, when automation is technically feasible.
- Review findings are not a report-only artifact: blockers must be fixed in code, tests, documentation, Issue criteria, or architecture and then re-reviewed.
- A requirement without observable evidence is not complete.
- Do not weaken tests, compatibility gates, branch governance, or validation merely to make CI green.
- Do not commit credentials, PATs, model keys, signing material, device secrets, or machine-local configuration.

## Definition of Done

A production task may be called complete only when all applicable items are satisfied:

- architecture and ownership boundaries are explicit;
- production implementation is complete;
- failure, cancellation, shutdown, and recovery behavior is defined;
- observability is sufficient to diagnose the supported failure modes;
- acceptance criteria are mapped to executable or repeatable evidence;
- risk-matched automated tests pass;
- required fault/contract/performance checks pass when applicable;
- compatibility and migration impact are addressed;
- required documentation is synchronized;
- Five-Gate Review has no unresolved blockers;
- exact-head required CI passes;
- unresolved review threads are zero;
- the resulting merge commit is verified on main.

## Validation authority

OpenHands usually runs in a Linux sandbox and uses .openhands/setup.sh plus .openhands/pre-commit.sh for pre-PR validation.

OpenDeviceStudio contains Windows/WPF targets. GitHub Actions on Windows remains the authoritative integration/release gate for Windows runtime, WPF, package, and source-scaffold behavior. Linux success must not be reported as Windows runtime/UI validation.
