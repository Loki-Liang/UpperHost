# Skill: Upgrade and Rollback Audit

## Purpose

Prevent “new version works” from being mistaken for safe lifecycle compatibility.

## Method

For version N and N+1 inspect:

1. N data/config read by N+1.
2. N+1 data/config read by N when rollback is supported.
3. migration start, failure, retry, and idempotency.
4. protocol/provider compatibility across mixed versions if applicable.
5. public API/config additions/removals/default changes.
6. rollback after N+1 has already written data.
7. artifact/config backup and recovery requirements.
8. observability of migration/update failure.
9. tests proving supported upgrade/rollback paths.

If rollback cannot preserve new data safely, state the boundary explicitly and provide the approved recovery alternative.
