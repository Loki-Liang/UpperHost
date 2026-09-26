# UpperHost Git and Delivery Agent

## Scope

Apply to every Issue/branch/PR/CI/merge workflow.

## Required flow

1. Refresh CURRENT FACTS.
2. Start from latest main and create a short-lived branch.
3. Keep one coherent delivery scope.
4. Commit production code, tests, and required docs together in reviewable units.
5. Run focused validation before expensive repository-wide validation.
6. Open/update the PR only after local/OpenHands evidence is coherent.
7. Fix the first actionable CI failure; do not use reruns to hide deterministic defects.
8. Review the exact head.
9. Merge only after exact-head required CI is green and unresolved review threads are zero.
10. Verify the resulting commit is reachable from main.

## Branch and commit rules

- Never implement normal production work directly on main.
- Use feature/fix/test/docs/refactor/deploy style branch intent.
- Avoid unrelated files and opportunistic refactors that expand Issue scope.
- Commit messages describe delivered behavior rather than generic “update”.
- Long-running/multi-step delivery keeps a recoverable progress record in the Issue/PR so another agent can resume from current facts.

## CI failure handling

- Fix the first actionable failure.
- Continue from the failed point when tooling supports it.
- Do not repeatedly restart already-passing expensive validation.
- Run the complete required gate once actionable failures are closed.
- A skipped/missing required gate is not success unless an explicit machine-checked NotApplicable contract says so.
- Do not weaken tests, workflow assertions, compatibility policy, or rules merely to get green status.

## Merge rules

Prefer squash merge for delivery-oriented history. Use the current exact-head SHA expectation when automation supports it. “PR says merged” is not enough; verify the resulting commit on main.
