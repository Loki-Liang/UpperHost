# OpenDeviceStudio Compatibility Agent

## Scope

Apply when public runtime APIs, configuration contracts, serialized/persisted formats, protocol compatibility, package surfaces, or the canonical source scaffold change.

## Required flow

1. Identify every versioned surface affected by the diff.
2. Compare against the exact current PR base, not an older convenient baseline.
3. Classify each change as compatible addition, compatible behavior correction, or breaking change.
4. Provide required approval/migration records.
5. Run compatibility gates and upgrade/rollback audit.
6. Ensure documentation describes migration and supported combinations.

## Repository compatibility contract

- Public runtime APIs, configuration contracts, and the canonical source scaffold are versioned surfaces.
- build-test-scaffold validates eng/compatibility/source-scaffold-contract.json and compares reusable NuGet packages against the exact PR base commit with Microsoft.DotNet.ApiCompat.Tool.
- The baseline comes from a successful CI artifact for the exact base SHA. Never fall back to an older successful main artifact.
- ApiCompat breaking changes require an explicit Issue, migration instructions, version decision, tests, and same-PR eng/compatibility/breaking/<PackageId>.md approval record.
- Deliberate public API additions require a same-PR eng/compatibility/api-additions/<PackageId>.md approval record.
- Never delete a baseline, disable ApiCompat/Package Validation, weaken the source-scaffold contract, or skip the gate to make CI green.
- Local pre-commit validates source-scaffold contract; exact-base package comparison remains authoritative PR/Windows CI.

## Required method skills

- .openhands/skills/upgrade-rollback-audit.md
- .openhands/skills/contract-attack.md

## Evidence

Compatibility decisions identify affected surface, exact base, gate result, migration/approval record when required, and rollback/old-reader behavior where persisted/config data is involved.
