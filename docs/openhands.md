# OpenHands integration

[简体中文](openhands.zh-CN.md) | English

UpperHost includes repository-native configuration for OpenHands V1 / Agent Canvas. OpenHands is a development tool only; it is not an UpperHost runtime dependency.

## Layered governance

OpenHands is the default implementation executor. Governance is split by responsibility:

    AGENTS.md
      -> applicable agents.*.md
      -> reusable .openhands/skills/*.md methods
      -> ADR + Issue acceptance/evidence
      -> implementation/tests
      -> Five-Gate Review
      -> CI evidence

- AGENTS.md is the thin repository constitution and rule router.
- .github/governance/agent-governance.json is the machine-readable routing manifest.
- agents.*.md files define long-lived specialty process/rules.
- .openhands/skills/*.md files define reusable methods such as adversarial review, pre-mortem, invariant analysis, state-machine audit, dataflow trace, capacity/backpressure audit, fault injection, contract attack, and upgrade/rollback audit.
- .openhands/skills/repo.md is the OpenHands execution profile. It loads rules; it does not duplicate them.

Skills explain **how** to perform analysis. Agent rules define **when** that analysis is mandatory.

## Default delivery chain

    CURRENT FACTS
      -> load applicable rules
      -> read existing architecture/code/tests
      -> Acceptance + Evidence Matrix
      -> implementation
      -> focused validation
      -> Five-Gate Review
      -> fix findings
      -> .openhands/pre-commit.sh
      -> Pull Request
      -> exact-head Windows GitHub Actions
      -> squash merge
      -> verify main

OpenHands must not stop after only reporting findings. Review blockers are fixed and re-reviewed.

## Five-Gate Review

agents.review.md defines the common production quality gate:

1. architecture attack;
2. failure attack;
3. implementation attack;
4. acceptance/evidence attack;
5. maintainer attack.

The review is backed by reusable method skills rather than one giant prompt.

## Repository hooks

- .openhands/setup.sh: idempotent workspace bootstrap for .NET 10 plus solution restore.
- .openhands/pre-commit.sh: layered governance validation, architecture/source-scaffold guards, build, and unit tests.
- scripts/validate_agent_governance.py: verifies root routing, specialty agents, OpenHands profile, and method skills cannot silently drift apart.
- GitHub Actions reruns governance validator/tests and remains the authoritative Windows integration gate.

## Platform validation boundary

OpenHands commonly runs in Linux while UpperHost contains Windows/WPF projects. Linux pre-PR validation uses:

    dotnet restore UpperHost.slnx -p:EnableWindowsTargeting=true
    dotnet build UpperHost.slnx -c Release -p:EnableWindowsTargeting=true
    dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release --no-build

Final Windows build, full tests, package compatibility, and source-scaffold validation remain GitHub Actions responsibilities.

## Recommended task instruction

A normal Issue assignment can now be short because policy lives in the repository:

    Implement this Issue from latest main.
    Follow AGENTS.md and load every applicable specialty agent from the governance manifest.
    Build the Acceptance/Evidence Matrix before coding.
    Apply required method skills, run focused validation, then Five-Gate Review.
    Fix review findings rather than only reporting them.
    Run .openhands/pre-commit.sh and leave the PR at an exact-head merge-ready state.

## Secrets

Never store OpenHands/model credentials, PATs, signing material, or device secrets in repository files.
