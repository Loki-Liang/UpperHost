# Main branch governance

[简体中文](main-branch-governance.zh-CN.md) | English

This document is the repository-side contract for Issue #31. UpperHost requires
GitHub-side branch protection/ruleset enforcement in addition to CI and AGENTS
rules. A passing repository workflow is not a substitute for an active GitHub
ruleset.

## Canonical required checks

The canonical required check job IDs are defined in
`.github/governance/main-branch-policy.json`:

- `openhands-config`
- `architecture-governance`
- `linux-runtime-contract`
- `build-test-scaffold`

`scripts/validate_main_governance.py` runs inside `openhands-config` and fails
when this contract drifts from `.github/workflows/ci.yml`.

Do not rename or delete one of these jobs without updating the policy file and
the active GitHub ruleset in the same delivery.

## Required GitHub ruleset

Create an active branch ruleset targeting `main` with these semantics:

1. Require changes to arrive through a pull request.
2. Require all canonical status checks above.
3. Require the branch to be up to date with `main` before merge
   (strict required status checks / freshness).
4. Require conversation resolution before merge.
5. Block force pushes.
6. Block branch deletion.
7. Do not allow ordinary direct pushes to `main`.
8. Avoid bypass actors. If an administrative emergency bypass must exist, it
   must be explicit and auditable.

A single-maintainer repository does not need to invent an approval requirement
only to satisfy this contract; the non-bypassable CI, freshness, and review
thread rules are the required baseline.

## Exact-head merge rule

Immediately before merge, re-read the PR and verify:

- base is `main`;
- head SHA is the SHA whose required checks passed;
- the branch satisfies the current-main freshness requirement;
- mergeability is not blocked;
- required checks are successful;
- unresolved review threads are zero.

After merge, verify the resulting merge/squash commit is reachable from
`main`. Never reuse a previous head's CI result.

## Administration boundary

Repository files cannot activate a GitHub ruleset by themselves. The ruleset is
an administrative repository setting. The connected GitHub integration used by
some automation environments may have contents/actions permissions while still
lacking repository-administration permission.

If the ruleset API returns `Resource not accessible by integration`, do not
report Issue #31 as complete. Apply the settings through an authorized GitHub
administrator context, then verify them through the repository ruleset API/UI.

## Verification evidence for #31

Before closing Issue #31, retain evidence that:

- an active ruleset targets `main`;
- the four canonical required checks are configured;
- strict/freshness behavior is enabled;
- a PR with a failed required check cannot merge;
- a PR with a missing required check cannot merge;
- direct push / force push / deletion behavior matches the policy;
- the documented check names match current workflow job IDs.

Public API Compatibility is enforced inside the existing canonical `build-test-scaffold` job, so Issue #41 does not introduce a new required-check job ID. The active ruleset continues to require `build-test-scaffold`, which now contains the compatibility steps. Any future gate that introduces a new job ID (for example a separately modeled Source-Scaffold E2E gate) must be added to both the policy and the active GitHub ruleset.
