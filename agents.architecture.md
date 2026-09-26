# UpperHost Architecture Agent

## Scope

Apply this rule whenever a change introduces or alters module boundaries, public contracts, state ownership, data ownership, lifecycle, a framework, a provider seam, or a distributed boundary.

## Required flow

1. Read AGENTS.md, the current Issue/PR, docs/architecture.md, relevant ADRs, code, tests, and current dependency graph.
2. Describe the existing architecture before proposing a change.
3. Answer the Architecture Seven Questions.
4. Run the applicable method skills.
5. Choose the smallest design that preserves all invariants and acceptance criteria.
6. Record a new ADR when the decision changes a durable public/module boundary, introduces a new framework, or creates a distributed boundary.
7. Map architecture claims to tests or executable guards.

## Architecture Seven Questions

Every architecture proposal must answer:

1. **Boundary:** what is this component responsible for, and what is explicitly outside its responsibility?
2. **Data ownership:** who creates, owns, mutates, persists, and disposes each important data object?
3. **State ownership:** which component is authoritative for each state and transition?
4. **Invariants:** what must never become false, including during failure and recovery?
5. **Lifecycle:** how does the component start, run, cancel, stop, fault, recover, and dispose?
6. **Capacity/backpressure:** where can work accumulate, what are the bounds, and what happens at saturation?
7. **Change axis:** when device/protocol/business requirements change in two years, which boundary is expected to change and which must remain stable?

If any answer is unclear, the architecture is not ready for implementation.

## UpperHost baseline

UpperHost is a modular monolith by default.

- UpperHost.Abstractions is the stable dependency root and must not reference another repository project.
- Platform modules expose narrow contracts and must not depend on Presentation, app, samples, or tests.
- UpperHost.Transport.* and storage/provider packages are infrastructure adapters and keep vendor/native concepts out of Core.
- UpperHost.Presentation.* is an outer adapter. Production modules do not depend back on Presentation.
- UpperHost.Starters is a composition/convenience module. Other production modules do not depend back on it.
- app/, samples/, and tests/ may compose production modules; production modules do not reference them.
- ProjectReference cycles are forbidden.
- Cross-module behavior uses public capabilities/contracts/events. Reflection or static coupling must not bypass boundaries.
- Public APIs stay minimal. Types remain internal/private unless another module genuinely needs the contract.
- A new project/module requires a clear responsibility, ownership boundary, allowed dependencies, and matching tests.
- A distributed boundary requires an explicit Issue/ADR and operational evidence; do not introduce microservices or remote RPC merely for code separation.

scripts/validate_architecture.py is an executable architecture guard and must remain green.

## Architecture invariants

- Core remains industry-neutral.
- Device behavior uses capability composition instead of a giant inheritance hierarchy.
- Transport owns transfer mechanics; Protocol owns framing and semantic decoding.
- Ordered bytes use ITransport; discrete messages/frames use IMessageTransport<TMessage>; vendor SDK domain APIs map to Device Capabilities.
- Request/response, message/frame, and streaming remain explicit boundaries.
- Vendor/native dependencies stay in provider packages, never UpperHost.Abstractions.
- UI and Workflow call application/device capabilities rather than sockets/native SDKs directly.
- Backpressure and loss policy are explicit for streaming.
- WPF is an adapter, not Core.
- Software interlocks never claim to replace certified hardware safety mechanisms.

## Required method skills

- .openhands/skills/invariant-analysis.md for every stateful or cross-module design.
- .openhands/skills/state-machine-audit.md when states/transitions exist.
- .openhands/skills/capacity-backpressure-audit.md for queues, channels, streams, batching, pools, or scheduler capacity.
- .openhands/skills/premortem.md for medium/high-risk architecture changes.

## Technology-choice challenge

For every new framework, package, abstraction layer, runtime mechanism, or distributed boundary answer:

- Why is it needed?
- What breaks if we do not add it?
- What simpler option was considered?
- What are the performance, operations, test, compatibility, and maintenance costs?
- Under what future condition should this choice be replaced?

“Modern”, “clean”, “enterprise”, or “best practice” is not an independent justification.
