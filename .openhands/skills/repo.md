# UpperHost repository instructions

## Purpose

UpperHost is a general-purpose .NET device-application platform. Control, automation, and acquisition are equal first-class paths. Do not turn the platform into a domain-specific, medical-only, acquisition-only, PLC-only, or vendor-specific framework.

## Required architecture boundaries

1. Keep Core industry-neutral.
2. Model devices by capability composition, not a giant inheritance tree.
3. Transport owns transfer mechanics; Protocol owns framing and semantic decoding.
4. Preserve native communication shape:
   - ordered bytes -> `ITransport`
   - discrete messages/frames -> `IMessageTransport<TMessage>`
   - vendor SDK domain operations -> Device Capability directly
5. Keep request/response, message/frame, and streaming as explicit boundaries.
6. Keep vendor/native SDK dependencies in provider packages; never place them in `UpperHost.Abstractions`.
7. UI/Workflow code calls application/device capabilities, not sockets or native SDKs directly.
8. Software guards/interlocks never claim to replace certified hardware safety mechanisms.
9. Backpressure and loss policy must be explicit for streaming paths.
10. WPF is an adapter. Do not couple Core to WPF.

## Repository map

- `src/UpperHost.Abstractions`: stable platform contracts.
- `src/UpperHost.Control`: command runtime, guards, interlocks, parameter readback.
- `src/UpperHost.Protocols`: protocol runtime and request/response semantics.
- `src/UpperHost.Transport.*`: transport/provider adapters.
- `src/UpperHost.Dataflow`: streaming fan-out/backpressure primitives.
- `src/UpperHost.Workflows` and `src/UpperHost.StateMachines`: automation orchestration.
- `src/UpperHost.Presentation.Wpf`: WPF adapter only.
- `samples/`: runnable Control / Automation / Acquisition examples.
- `tests/UpperHost.Tests`: automated platform tests.
- `templates/`: `dotnet new upperhost` template.
- `docs/`: bilingual architecture and extension guidance.

## Development workflow

1. Re-read current `main`, the issue/PR acceptance criteria, relevant code, tests, and CI before editing.
2. Work from a short-lived branch with one coherent scope.
3. Change production code and matching tests together.
4. For docs/examples that have an English/Chinese pair, update both in the same change.
5. Run focused validation first. Fix the first actionable failure and continue from that failure point; do not repeatedly restart already-passing validation.
6. After focused validation passes, run the repository validation below.
7. Review the final diff for architecture-boundary violations, missing tests, unrelated files, and stale documentation before opening/merging a PR.

## Validation

OpenHands usually runs in a Linux sandbox. The solution contains Windows/WPF targets, so Linux validation must enable Windows targeting:

```bash
dotnet restore UpperHost.slnx -p:EnableWindowsTargeting=true
dotnet build UpperHost.slnx -c Release -p:EnableWindowsTargeting=true
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release --no-build
```

The authoritative release gate remains the Windows GitHub Actions CI, which additionally packs all libraries and smoke-builds the generated WPF template. Never claim Windows runtime/UI validation from a Linux-only OpenHands run.

## OpenHands hooks

- `.openhands/setup.sh` prepares .NET 10 and restores the repository.
- `.openhands/pre-commit.sh` runs the Linux-safe build/test gate.
- Do not commit credentials, OpenHands API keys, model API keys, PATs, signing material, or machine-local configuration.
