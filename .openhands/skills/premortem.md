# Skill: Pre-mortem

## Purpose

Find production failure paths before implementation is declared complete.

## Method

Assume the implementation has been in production for six months and a serious incident has just occurred.

1. Generate the ten most plausible technical causes within task scope.
2. For each cause record the trigger, affected invariant/data/state, blast radius, detection, recovery, current preventive control, and missing control/evidence.
3. Rank for engineering triage without pretending to predict future probability.
4. Fix high-impact missing controls that are in scope.
5. Add fault/contract/observability evidence that the control actually works.

A pre-mortem is incomplete if it only produces a risk list.
