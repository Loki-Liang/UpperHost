#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
COVERAGE_PATH = ROOT / "eng" / "acquisition-verification" / "coverage.json"
BASELINE_PATH = ROOT / "eng" / "acquisition-verification" / "performance-baseline.json"

COMPONENT_KEYS = {"status", "issue", "evidence", "note"}
STATUSES = {"Production", "Blocked", "NotApplicable"}
BASELINE_KEYS = {
    "schemaVersion", "status", "environmentId", "sourceSha",
    "updatedAtUtc", "environmentFingerprintSha256", "policy", "benchmarks",
}
POLICY_KEYS = {
    "maxMeanRegressionPercent", "maxAllocationRegressionPercent",
    "minimumMeasuredIterations", "requiresExplicitReviewForBaselineChange",
}


class ValidationError(ValueError):
    pass


def _load(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValidationError(f"{path}: root must be an object")
    return value


def validate_coverage(value: dict) -> None:
    if set(value) != {"schemaVersion", "components", "requiredForIssueClosure"}:
        raise ValidationError("coverage.json has unknown or missing top-level fields")
    if value["schemaVersion"] != 1:
        raise ValidationError("coverage.json schemaVersion must be 1")
    components = value["components"]
    required = value["requiredForIssueClosure"]
    if not isinstance(components, dict) or not components:
        raise ValidationError("coverage.json components must be a non-empty object")
    if not isinstance(required, list) or not required or len(required) != len(set(required)):
        raise ValidationError("requiredForIssueClosure must be a non-empty unique array")

    for component_id, component in components.items():
        if not isinstance(component_id, str) or not component_id:
            raise ValidationError("component id must be non-empty")
        if not isinstance(component, dict) or set(component) != COMPONENT_KEYS:
            raise ValidationError(f"component {component_id}: invalid fields")
        if component["status"] not in STATUSES:
            raise ValidationError(f"component {component_id}: invalid status {component['status']!r}")
        if not isinstance(component["issue"], int) or component["issue"] <= 0:
            raise ValidationError(f"component {component_id}: issue must be a positive integer")
        if not isinstance(component["evidence"], list) or any(
            not isinstance(item, str) or not item for item in component["evidence"]
        ):
            raise ValidationError(f"component {component_id}: evidence must be a string array")
        if component["status"] == "Production" and not component["evidence"]:
            raise ValidationError(f"component {component_id}: Production requires evidence")
        if not isinstance(component["note"], str) or not component["note"]:
            raise ValidationError(f"component {component_id}: note is required")

    missing = [item for item in required if item not in components]
    if missing:
        raise ValidationError(f"requiredForIssueClosure references unknown component(s): {missing}")


def validate_baseline(value: dict) -> None:
    if set(value) != BASELINE_KEYS:
        raise ValidationError("performance-baseline.json has unknown or missing fields")
    if value["schemaVersion"] != 1:
        raise ValidationError("performance baseline schemaVersion must be 1")
    if value["status"] not in {"Uninitialized", "Active"}:
        raise ValidationError("performance baseline status must be Uninitialized or Active")
    if not isinstance(value["environmentId"], str) or not value["environmentId"]:
        raise ValidationError("performance baseline environmentId is required")
    if not isinstance(value["policy"], dict) or set(value["policy"]) != POLICY_KEYS:
        raise ValidationError("performance baseline policy fields are invalid")
    policy = value["policy"]
    for name in ("maxMeanRegressionPercent", "maxAllocationRegressionPercent"):
        if not isinstance(policy[name], (int, float)) or isinstance(policy[name], bool) or policy[name] < 0:
            raise ValidationError(f"performance baseline {name} must be non-negative")
    if not isinstance(policy["minimumMeasuredIterations"], int) or policy["minimumMeasuredIterations"] <= 0:
        raise ValidationError("minimumMeasuredIterations must be positive")
    if policy["requiresExplicitReviewForBaselineChange"] is not True:
        raise ValidationError("baseline changes must require explicit review")
    if not isinstance(value["benchmarks"], dict):
        raise ValidationError("performance baseline benchmarks must be an object")
    if value["status"] == "Active":
        if (
            not value["sourceSha"]
            or not value["updatedAtUtc"]
            or not value["environmentFingerprintSha256"]
            or not value["benchmarks"]
        ):
            raise ValidationError(
                "Active performance baseline requires sourceSha, updatedAtUtc, environment fingerprint, and measurements"
            )


def release_blockers(coverage: dict, baseline: dict) -> list[str]:
    blockers: list[str] = []
    components = coverage["components"]
    for component_id in coverage["requiredForIssueClosure"]:
        component = components[component_id]
        if component["status"] != "Production":
            blockers.append(
                f"{component_id}: {component['status']} (issue #{component['issue']})"
            )
    if baseline["status"] != "Active":
        blockers.append(
            f"stable performance baseline: {baseline['status']} ({baseline['environmentId']})"
        )
    return blockers


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--require-release-ready",
        action="store_true",
        help="Fail when any #62 closure dependency or stable performance baseline is not production-ready.",
    )
    args = parser.parse_args()

    try:
        coverage = _load(COVERAGE_PATH)
        baseline = _load(BASELINE_PATH)
        validate_coverage(coverage)
        validate_baseline(baseline)
        blockers = release_blockers(coverage, baseline)
    except (OSError, json.JSONDecodeError, ValidationError) as exc:
        print(f"Acquisition release-readiness validation failed: {exc}", file=sys.stderr)
        return 1

    if args.require_release_ready and blockers:
        print("Acquisition release gate is BLOCKED/NOT_COMPARABLE:", file=sys.stderr)
        for blocker in blockers:
            print(f"- {blocker}", file=sys.stderr)
        return 2

    if blockers:
        print("Acquisition verification structure: OK; closure blockers are explicit:")
        for blocker in blockers:
            print(f"- {blocker}")
    else:
        print("Acquisition verification release readiness: READY")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
