# Skill: Invariant Analysis

## Purpose

Design systems around truths that remain valid in normal, concurrent, failure, and recovery paths.

## Method

For each stateful subsystem:

1. List invariants in precise language.
2. Name the authoritative owner enforcing each invariant.
3. Identify every transition/code path that can violate it.
4. Define behavior on partial failure, cancellation, restart, and retry.
5. Define how violation is detected.
6. Add tests/guards that fail when the invariant is broken.

Examples:

- at most one active receive loop owns a connection;
- a stopped session accepts no new samples;
- a Required acquisition branch cannot silently lose accepted data;
- a persisted sequence gap is observable rather than fabricated away.

Avoid vague invariants such as “system remains stable”.
