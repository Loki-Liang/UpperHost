# OpenHands integration

[简体中文](openhands.zh-CN.md) | English

UpperHost includes repository-native configuration for OpenHands V1 / Agent Canvas. OpenHands is a development tool only; it is not an UpperHost runtime dependency.

## Default development policy

OpenHands is the **default implementation executor** for normal UpperHost feature, fix, refactor, test, documentation, and architecture work. `AGENTS.md` is the repository-level authority and `.openhands/skills/repo.md` is the OpenHands execution profile.

The normal delivery chain is:

```text
Issue/Task -> OpenHands -> focused validation -> .openhands/pre-commit.sh
          -> Pull Request -> Windows GitHub Actions -> review -> squash merge -> main
```

Other assistants may coordinate, inspect, or review work, but production implementation should remain in OpenHands unless a fallback condition documented in `AGENTS.md` applies.

## What is integrated

- `.openhands/skills/repo.md`: automatically loaded repository guidance covering architecture, repository layout, workflow, and validation.
- `.openhands/setup.sh`: idempotent workspace bootstrap for .NET 10 plus solution restore.
- `.openhands/pre-commit.sh`: build and unit-test gate suitable for the Linux sandbox.
- GitHub Actions validates the OpenHands shell hooks so repository configuration cannot silently rot.

## Connect the repository

Use a current OpenHands V1 / Agent Canvas installation or OpenHands Cloud, connect GitHub, then select:

```text
Loki-Liang/UpperHost
```

Start each implementation from current `main` and let the repository skill load before editing.

For a local Agent Canvas installation, use the current OpenHands installation instructions. A sandboxed setup is recommended when the agent should not have unrestricted access to the host filesystem.

## Platform validation boundary

UpperHost contains WPF projects and the authoritative CI runs on Windows. OpenHands commonly runs in a Linux sandbox.

The repository hooks therefore use:

```bash
dotnet restore UpperHost.slnx -p:EnableWindowsTargeting=true
dotnet build UpperHost.slnx -c Release -p:EnableWindowsTargeting=true
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release --no-build
```

This gives OpenHands fast compile/test feedback before code is pushed. The final GitHub Actions gate still performs the Windows build, full tests, NuGet packing, and generated-template smoke build.

## Secrets

Do not place any OpenHands or model credential in this repository. Configure authentication in OpenHands itself or in the relevant secret store. Repository files must contain only non-secret setup and policy.

## Recommended task prompt

When assigning an issue to OpenHands, make the task concrete and require closure:

```text
Implement this issue against the latest main. Read .openhands/skills/repo.md first.
Keep the existing architecture boundaries. Add or update tests with the production change.
Run focused validation, then the repository pre-commit gate. Review the final diff and
leave the branch/PR in a merge-ready state; do not stop after only reporting findings.
```
