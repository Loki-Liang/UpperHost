#!/usr/bin/env python3
"""Validate an active GitHub branch ruleset against UpperHost's repository policy.

The validator is intentionally read-only and uses only the Python standard library.
It validates a ruleset JSON document previously fetched from GitHub, for example:

    gh api repos/Loki-Liang/UpperHost/rulesets/<id> \
      | python scripts/validate_main_ruleset.py --ruleset -

The script never mutates GitHub settings.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_POLICY_PATH = ROOT / ".github" / "governance" / "main-branch-policy.json"


def _read_json(path: str | Path) -> dict[str, Any]:
    if str(path) == "-":
        return json.load(sys.stdin)
    with Path(path).open("r", encoding="utf-8") as handle:
        return json.load(handle)


def _rule_map(ruleset: dict[str, Any]) -> dict[str, dict[str, Any]]:
    rules = ruleset.get("rules")
    if not isinstance(rules, list):
        return {}
    result: dict[str, dict[str, Any]] = {}
    for rule in rules:
        if isinstance(rule, dict) and isinstance(rule.get("type"), str):
            result[rule["type"]] = rule
    return result


def _normalize_bypass(actor: dict[str, Any]) -> tuple[str, int | str | None, str]:
    return (
        str(actor.get("actor_type", "")),
        actor.get("actor_id"),
        str(actor.get("bypass_mode", "")),
    )


def validate_ruleset(
    policy: dict[str, Any],
    ruleset: dict[str, Any],
) -> list[str]:
    errors: list[str] = []

    if ruleset.get("enforcement") != "active":
        errors.append("ruleset enforcement must be active")

    conditions = ruleset.get("conditions", {})
    ref_name = conditions.get("ref_name", {}) if isinstance(conditions, dict) else {}
    include = set(ref_name.get("include", []) or [])
    exclude = set(ref_name.get("exclude", []) or [])
    branch = policy.get("branch", "main")
    accepted_targets = {"~DEFAULT_BRANCH", branch, f"refs/heads/{branch}"}
    if not (include & accepted_targets):
        errors.append(f"ruleset must target the protected branch '{branch}'")
    if "~DEFAULT_BRANCH" in exclude or branch in exclude or f"refs/heads/{branch}" in exclude:
        errors.append(f"ruleset must not exclude the protected branch '{branch}'")

    rules = _rule_map(ruleset)

    if policy.get("require_pull_request") is True and "pull_request" not in rules:
        errors.append("pull_request rule is required")
    if policy.get("block_deletions") is True and "deletion" not in rules:
        errors.append("deletion rule is required")
    if policy.get("block_force_pushes") is True and "non_fast_forward" not in rules:
        errors.append("non_fast_forward rule is required")

    pull_request = rules.get("pull_request", {})
    pull_parameters = pull_request.get("parameters", {}) if isinstance(pull_request, dict) else {}
    if (
        policy.get("require_conversation_resolution") is True
        and pull_parameters.get("required_review_thread_resolution") is not True
    ):
        errors.append("required_review_thread_resolution must be true")

    status_rule = rules.get("required_status_checks")
    if not isinstance(status_rule, dict):
        errors.append("required_status_checks rule is required")
    else:
        status_parameters = status_rule.get("parameters", {})
        if (
            policy.get("strict_required_status_checks") is True
            and status_parameters.get("strict_required_status_checks_policy") is not True
        ):
            errors.append("strict_required_status_checks_policy must be true")

        actual_checks: set[str] = set()
        raw_checks = status_parameters.get("required_status_checks", [])
        if isinstance(raw_checks, list):
            for check in raw_checks:
                if isinstance(check, dict) and isinstance(check.get("context"), str):
                    actual_checks.add(check["context"])
                elif isinstance(check, str):
                    actual_checks.add(check)

        expected_checks = set(policy.get("required_status_checks", []) or [])
        missing = sorted(expected_checks - actual_checks)
        unexpected = sorted(actual_checks - expected_checks)
        if missing:
            errors.append("missing required status checks: " + ", ".join(missing))
        if unexpected:
            errors.append("unexpected required status checks: " + ", ".join(unexpected))

    expected_bypass = {
        _normalize_bypass(actor)
        for actor in policy.get("allowed_bypass_actors", []) or []
        if isinstance(actor, dict)
    }
    actual_bypass = {
        _normalize_bypass(actor)
        for actor in ruleset.get("bypass_actors", []) or []
        if isinstance(actor, dict)
    }
    if actual_bypass != expected_bypass:
        unexpected = sorted(actual_bypass - expected_bypass, key=str)
        missing = sorted(expected_bypass - actual_bypass, key=str)
        if unexpected:
            errors.append("unexpected bypass actors: " + ", ".join(map(str, unexpected)))
        if missing:
            errors.append("missing approved bypass actors: " + ", ".join(map(str, missing)))

    return errors


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--policy", default=str(DEFAULT_POLICY_PATH))
    parser.add_argument(
        "--ruleset",
        required=True,
        help="Ruleset JSON file produced by GitHub REST API, or '-' for stdin.",
    )
    args = parser.parse_args()

    policy = _read_json(args.policy)
    ruleset = _read_json(args.ruleset)
    errors = validate_ruleset(policy, ruleset)

    if errors:
        print("main-ruleset validation failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        raise SystemExit(1)

    print("main-ruleset validation passed")
    print("ruleset:", ruleset.get("name", "<unnamed>"), ruleset.get("id", "<no-id>"))
    print("required checks:", ", ".join(policy.get("required_status_checks", [])))


if __name__ == "__main__":
    main()
