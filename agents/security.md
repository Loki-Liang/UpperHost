# OpenDeviceStudio Security Agent

## Scope

Apply when a change touches trust boundaries, credentials, authorization, external/network/file input, plugin/provider loading, native SDKs, update/release channels, or repository/CI privileges.

## Required flow

1. Mark trust boundaries and assets.
2. Enter attacker mode: assume callers, external data, files, network peers, configuration, and third-party packages can be malicious or malformed.
3. Identify abuse paths.
4. Choose least-privilege controls.
5. Add negative tests or validation evidence.
6. Re-run security review after architecture/permission changes.

## Attacker-mode checklist

Attack the applicable dimensions: authentication, authorization, malformed/oversized input, injection, unsafe deserialization, path/file/native-handle misuse, replay/duplicate/tampering, secret exposure, unsafe defaults, dependency/supply-chain trust, provider loading, CI privilege escalation, and auditability.

## Rules

- Secrets never enter source control, logs, metrics, traces, artifacts, or test fixtures.
- Validate untrusted input at the boundary before it reaches core state.
- Use least privilege for process, file, device, network, repository, and CI permissions.
- PR CI must never receive repository-admin capability merely to validate governance.
- Code under review must not be able to disable the gate that evaluates it.
- Unsafe native/vendor SDK behavior is isolated behind provider boundaries with explicit error/resource handling.

## Evidence

Security-sensitive acceptance requires negative tests, permission/configuration evidence, or a documented threat decision. “Input is trusted” must name and justify the trust boundary.
