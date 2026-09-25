# Compatibility policy

[简体中文](compatibility.zh-CN.md) | English

UpperHost is source-first, but reusable runtime modules and the canonical application scaffold are still versioned contracts. Compatibility is enforced in the existing required `build-test-scaffold` job so the gate cannot become an optional side workflow.

## Protected surfaces

The gate protects three surfaces:

1. **Source scaffold** — `app/UpperHost.App`, its source-first `ProjectReference` topology, startup composition, WPF target and required configuration paths.
2. **Runtime package APIs** — every reusable `src/UpperHost.*` package produced by CI, including abstractions, hosting, control, dataflow, workflows, state machines, providers/starters and published presentation contracts.
3. **Optional NuGet distribution** — when those runtime packages are released, the same package/API compatibility rules apply. NuGet remains optional for cross-repository reuse; it is not required to clone/fork and run the scaffold.

The source contract is declared in `eng/compatibility/source-scaffold-contract.json` and validated by `scripts/validate_source_scaffold_contract.py`.

## Exact-base API comparison

PR CI packs the current runtime modules, then downloads `upperhost-packages` from the successful main CI run whose head SHA is **exactly** the PR base SHA. It refuses to fall back to an older successful run.

The comparison uses Microsoft's `Microsoft.DotNet.ApiCompat.Tool`:

- normal baseline validation detects removed/renamed members, incompatible signatures and package compatibility breaks;
- strict baseline validation also identifies additive public API drift, preventing accidental expansion of the supported contract;
- CI includes a self-test that proves a removed public member fails normal validation and an added public member is detected in strict mode.

This is intentionally not a repository-specific API diff parser.

## Deliberate public API additions

If an API is intentionally public, add or modify this file in the same PR:

```text
eng/compatibility/api-additions/<PackageId>.md
```

It must contain non-empty fields:

```text
Issue: #123
Reason: why this must be public
Surface: types/members being added
```

An approval file from an older PR cannot authorize a new change; the gate checks that the file changed relative to the current PR base.

## Breaking changes

A deliberate breaking change requires all of the following in the same delivery:

1. an explicit Issue/PR that marks the break and explains impact;
2. tests covering the new contract;
3. migration instructions;
4. an appropriate SemVer/package version change;
5. release notes/migration notes for the release;
6. a changed `eng/compatibility/breaking/<PackageId>.md` containing:

```text
Issue: #123
Reason: why compatibility cannot be preserved
Migration: exact consumer migration instructions
Version: new version / SemVer rationale
```

Deleting baselines, disabling ApiCompat, weakening the source contract or skipping the compatibility step is never an acceptable way to resolve a failure.

## Configuration and source-scaffold changes

`app/UpperHost.App` remains the canonical clone/fork product entry. The contract checks its WPF target, source `ProjectReference` dependencies, startup composition and stable configuration paths. Changing one of those surfaces requires an explicit contract update plus migration/documentation and matching tests.

The pre-commit hook runs this source-scaffold contract locally. Package comparison is authoritative in PR Windows CI because it consumes the validated exact-base package artifact.

## Release integration

Issue #27 already requires the compatibility gate before tag/GitHub Release/optional NuGet publish. A release must therefore reuse the same compatibility policy and must not bypass the PR evidence.

Issue #42 owns the deeper clean source-scaffold E2E. This gate protects the structural/startup/configuration contract and keeps the existing source-scaffold build validation active; it does not claim to replace #42.
