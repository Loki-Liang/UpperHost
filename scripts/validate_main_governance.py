#!/usr/bin/env python3
"""Validate the repository-side contract for main branch governance.

This validator intentionally uses only the Python standard library so it can
run in OpenHands and GitHub Actions without another dependency.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
POLICY_PATH = ROOT / ".github" / "governance" / "main-branch-policy.json"
WORKFLOW_PATH = ROOT / ".github" / "workflows" / "ci.yml"


def fail(message: str) -> None:
    print(f"main-governance validation failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def workflow_job_ids(workflow: str) -> set[str]:
    jobs: set[str] = set()
    in_jobs = False

    for raw_line in workflow.splitlines():
        if raw_line == "jobs:":
            in_jobs = True
            continue

        if not in_jobs:
            continue

        if raw_line and not raw_line.startswith(" "):
            break

        match = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", raw_line)
        if match:
            jobs.add(match.group(1))

    return jobs


def main() -> None:
    if not POLICY_PATH.is_file():
        fail(f"missing policy file: {POLICY_PATH.relative_to(ROOT)}")
    if not WORKFLOW_PATH.is_file():
        fail(f"missing workflow: {WORKFLOW_PATH.relative_to(ROOT)}")

    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    workflow = WORKFLOW_PATH.read_text(encoding="utf-8")

    if policy.get("version") != 1:
        fail("policy version must be 1")
    if policy.get("branch") != "main":
        fail("protected branch contract must target main")

    boolean_requirements = (
        "strict_required_status_checks",
        "require_pull_request",
        "block_direct_push",
        "block_force_pushes",
        "block_deletions",
        "require_conversation_resolution",
        "require_exact_head_validation",
        "admin_application_required",
    )
    for key in boolean_requirements:
        if policy.get(key) is not True:
            fail(f"{key} must be true")

    required_checks = policy.get("required_status_checks")
    if not isinstance(required_checks, list) or not required_checks:
        fail("required_status_checks must be a non-empty list")
    if len(required_checks) != len(set(required_checks)):
        fail("required_status_checks contains duplicates")

    jobs = workflow_job_ids(workflow)
    missing_jobs = sorted(set(required_checks) - jobs)
    if missing_jobs:
        fail(
            "required status checks do not map to CI job ids: "
            + ", ".join(missing_jobs)
        )

    if "pull_request_target:" in workflow:
        fail("CI governance workflow must not use pull_request_target")
    if "pull_request:" not in workflow:
        fail("CI must run for pull_request")
    if "push:" not in workflow:
        fail("CI must run for push")
    if "branches: [main]" not in workflow:
        fail("CI must explicitly target main")

    print("main-governance validation passed")
    print("required checks:", ", ".join(required_checks))


if __name__ == "__main__":
    main()
