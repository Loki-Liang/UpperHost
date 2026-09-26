from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "validate_main_ruleset.py"
SPEC = importlib.util.spec_from_file_location("validate_main_ruleset", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ValidateMainRulesetTests(unittest.TestCase):
    def setUp(self) -> None:
        self.policy = {
            "branch": "main",
            "required_status_checks": [
                "openhands-config",
                "architecture-governance",
                "linux-runtime-contract",
                "build-test-scaffold",
            ],
            "strict_required_status_checks": True,
            "require_pull_request": True,
            "block_force_pushes": True,
            "block_deletions": True,
            "require_conversation_resolution": True,
            "allowed_bypass_actors": [],
        }
        self.ruleset = {
            "id": 1,
            "name": "main",
            "enforcement": "active",
            "conditions": {
                "ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}
            },
            "rules": [
                {"type": "deletion"},
                {"type": "non_fast_forward"},
                {
                    "type": "pull_request",
                    "parameters": {"required_review_thread_resolution": True},
                },
                {
                    "type": "required_status_checks",
                    "parameters": {
                        "strict_required_status_checks_policy": True,
                        "required_status_checks": [
                            {"context": name}
                            for name in self.policy["required_status_checks"]
                        ],
                    },
                },
            ],
            "bypass_actors": [],
        }

    def errors(self) -> list[str]:
        return MODULE.validate_ruleset(self.policy, self.ruleset)

    def test_valid_ruleset_matches_policy(self) -> None:
        self.assertEqual([], self.errors())

    def test_missing_required_check_is_rejected(self) -> None:
        status_rule = next(
            rule
            for rule in self.ruleset["rules"]
            if rule["type"] == "required_status_checks"
        )
        status_rule["parameters"]["required_status_checks"].pop()
        self.assertTrue(
            any("missing required status checks" in error for error in self.errors())
        )

    def test_strict_freshness_must_be_enabled(self) -> None:
        status_rule = next(
            rule
            for rule in self.ruleset["rules"]
            if rule["type"] == "required_status_checks"
        )
        status_rule["parameters"]["strict_required_status_checks_policy"] = False
        self.assertIn(
            "strict_required_status_checks_policy must be true",
            self.errors(),
        )

    def test_review_thread_resolution_must_be_enabled(self) -> None:
        pull_rule = next(
            rule for rule in self.ruleset["rules"] if rule["type"] == "pull_request"
        )
        pull_rule["parameters"]["required_review_thread_resolution"] = False
        self.assertIn(
            "required_review_thread_resolution must be true",
            self.errors(),
        )

    def test_unapproved_bypass_actor_is_rejected(self) -> None:
        self.ruleset["bypass_actors"] = [
            {
                "actor_type": "RepositoryRole",
                "actor_id": 5,
                "bypass_mode": "always",
            }
        ]
        self.assertTrue(
            any("unexpected bypass actors" in error for error in self.errors())
        )

    def test_ruleset_must_target_default_branch(self) -> None:
        self.ruleset["conditions"]["ref_name"]["include"] = ["refs/heads/develop"]
        self.assertIn(
            "ruleset must target the protected branch 'main'",
            self.errors(),
        )


if __name__ == "__main__":
    unittest.main()
