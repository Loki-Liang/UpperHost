# OpenDeviceStudio Release Agent

## Scope

Apply to package/release readiness, deployment/update behavior, release baselines, installer/distribution work, and any change that can affect upgrade or rollback.

## Required flow — Release Eight Gates

A release is not ready until the applicable gates are proven:

1. **Build:** reproducible release configuration builds successfully.
2. **Configuration:** defaults, validation, environment/machine settings, and secrets are correct.
3. **Migration/Data:** existing data/config can be read or migrated with a defined failure path.
4. **Compatibility:** public API/config/source-scaffold/protocol compatibility decisions are explicit.
5. **Deployment/Packaging:** package/install/update steps are repeatable and artifact identity is known.
6. **Health:** startup and critical dependencies reach a defined healthy/ready state.
7. **Observability:** operators can diagnose startup/update/runtime failure.
8. **Rollback:** the previous supported version can be restored safely, including after the new version has written data/config.

## Rules

- Every formal release has a distinct Release Baseline/evidence set; do not rely on “main was green sometime”.
- Windows GitHub Actions is authoritative for Windows/WPF build/runtime/package integration.
- Linux/OpenHands success is pre-PR evidence, not Windows release evidence.
- Rollback considers data/config written by the new version.
- Release cannot depend on bypassing branch/CI governance.
- Compatibility exceptions require explicit approval/migration records, not disabled gates.

## Required method skills

- .openhands/skills/upgrade-rollback-audit.md
- .openhands/skills/premortem.md for medium/high-risk release changes.
- .openhands/skills/contract-attack.md for install/update/config public contracts.

## Evidence

Release evidence identifies source SHA, artifact/build identity, required CI runs, compatibility/migration results, health verification, and rollback verification appropriate to scope.
