# Skill: State Machine Audit

## Purpose

Eliminate impossible state combinations and race-prone boolean state.

## Method

1. Enumerate states.
2. Enumerate external/internal events.
3. Build the legal transition table.
4. Define illegal transitions and returned errors.
5. Define timeout, cancellation, fault, reconnect/retry, stop, and dispose transitions.
6. Define the single state authority.
7. Check duplicate/concurrent events such as Start+Start, Stop+Stop, Start+Stop, disconnect during connect, and Dispose during recovery.
8. Define terminal and recoverable fault semantics.
9. Add transition tests, including illegal/concurrent transitions.
10. Confirm logs/metrics/snapshots expose enough state for diagnosis.

If behavior still depends on unrelated booleans that can form invalid combinations, the audit fails.
