# UpperHost OpenHands Execution Profile

## Authority

AGENTS.md is the repository-level authority. This profile tells OpenHands how to load and execute the governance system; it must never weaken repository rules.

OpenHands is the default implementation executor for normal UpperHost feature, fix, refactor, test, documentation, and architecture work.

## Rule loading

Start with AGENTS.md, then read .github/governance/agent-governance.json.

All production-code work loads:

- agents.implementation.md
- agents.testing.md
- agents.review.md
- agents.git.md

Load additional rules by scope:

- architecture/module/public boundary -> agents.architecture.md
- TCP/Serial/USB/BLE/reconnect -> agents.transport.md
- DAQ/EMG/raw/processing/fan-out -> agents.acquisition.md
- logs/metrics/traces/health -> agents.observability.md
- trust/credentials/external input -> agents.security.md
- package/release/rollback -> agents.release.md
- API/config/source-scaffold compatibility -> agents.compatibility.md

Do not blindly copy every rule into task context. Load the root policy, base production rules, and only additional specialty rules that apply.

## Method skills

Specialty agents invoke reusable method skills as needed:

- .openhands/skills/adversarial-review.md
- .openhands/skills/premortem.md
- .openhands/skills/invariant-analysis.md
- .openhands/skills/state-machine-audit.md
- .openhands/skills/dataflow-trace.md
- .openhands/skills/capacity-backpressure-audit.md
- .openhands/skills/fault-injection.md
- .openhands/skills/contract-attack.md
- .openhands/skills/upgrade-rollback-audit.md

Skills are methods, not policy. They explain how to perform an analysis; AGENTS.md and agents.*.md define when it is mandatory.

## Required execution order

1. Refresh CURRENT FACTS from GitHub/repository.
2. Load applicable rules.
3. Read existing architecture/code/tests/ADRs before editing.
4. Confirm measurable acceptance criteria and build the Evidence Matrix.
5. Implement production behavior and matching tests.
6. Run focused validation.
7. Run agents.review.md Five-Gate Review and apply relevant method skills.
8. Fix findings; never stop at a findings-only report.
9. Run .openhands/pre-commit.sh.
10. Push/open PR and use exact-head GitHub Actions as the authoritative integration gate.
11. Merge only after required CI passes and review threads are clear; verify the result on main.

## Repository validation

Linux/OpenHands pre-PR commands:

    dotnet restore UpperHost.slnx -p:EnableWindowsTargeting=true
    dotnet build UpperHost.slnx -c Release -p:EnableWindowsTargeting=true
    dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release --no-build

The authoritative Windows/WPF integration gate remains GitHub Actions.

## Hooks and secrets

- .openhands/setup.sh prepares .NET 10 and restores the repository.
- .openhands/pre-commit.sh validates AI governance, architecture, source-scaffold compatibility, build, and tests.
- Never commit OpenHands credentials, model API keys, PATs, signing material, device secrets, or machine-local configuration.
