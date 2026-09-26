# Skill: Adversarial Review

## Purpose

Prevent self-confirming design review.

## Method

1. Assume the current design is wrong.
2. List at least five realistic conditions that could falsify its assumptions.
3. For each condition, identify the invariant or acceptance criterion that would fail.
4. Search code/tests/evidence for proof that the failure is controlled.
5. If proof is missing, change the design or acceptance criteria and add evidence.
6. Repeat until remaining risks are explicitly accepted rather than ignored.

Do not finish by saying “the design is reasonable”. Finish with falsifiable evidence or a recorded residual risk.
