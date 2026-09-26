from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "validate_agent_governance.py"
SPEC = importlib.util.spec_from_file_location("validate_agent_governance", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ValidateAgentGovernanceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / ".github/governance").mkdir(parents=True)
        (self.root / ".openhands/skills").mkdir(parents=True)\n        (self.root / "agents").mkdir(parents=True)

        self.manifest = {
            "version": 1,
            "root_policy": "AGENTS.md",
            "localized_root_policy": "AGENTS.zh-CN.md",
            "openhands_profile": ".openhands/skills/repo.md",
            "specialty_agents": ["agents/review.md", "agents/testing.md"],
            "method_skills": [
                ".openhands/skills/adversarial-review.md",
                ".openhands/skills/fault-injection.md",
            ],
            "root_required_markers": ["## Mandatory delivery flow"],
            "localized_root_required_markers": ["## 强制交付流程"],
            "profile_required_markers": ["## Rule loading"],
        }
        (self.root / ".github/governance/agent-governance.json").write_text(
            json.dumps(self.manifest), encoding="utf-8"
        )
        (self.root / "AGENTS.md").write_text(
            "## Mandatory delivery flow\n"
            ".github/governance/agent-governance.json\n"
            "agents/review.md\nagents/testing.md\n",
            encoding="utf-8",
        )
        (self.root / "AGENTS.zh-CN.md").write_text(
            "## 强制交付流程\n"
            "agents/review.md\nagents/testing.md\n",
            encoding="utf-8",
        )
        (self.root / ".openhands/skills/repo.md").write_text(
            "## Rule loading\n"
            ".github/governance/agent-governance.json\n"
            "agents/review.md\nagents/testing.md\n"
            ".openhands/skills/adversarial-review.md\n",
            encoding="utf-8",
        )
        (self.root / "agents/review.md").write_text(
            "Five-Gate Review\n.openhands/skills/adversarial-review.md\n",
            encoding="utf-8",
        )
        (self.root / "agents/testing.md").write_text(
            "Evidence Matrix\n.openhands/skills/fault-injection.md\n",
            encoding="utf-8",
        )
        for skill in self.manifest["method_skills"]:
            (self.root / skill).write_text("# skill\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def errors(self) -> list[str]:
        return MODULE.validate_governance(self.root)

    def test_valid_layered_governance_passes(self) -> None:
        self.assertEqual([], self.errors())

    def test_missing_specialty_agent_is_rejected(self) -> None:
        (self.root / "agents/testing.md").unlink()
        self.assertTrue(
            any("missing governance file: agents/testing.md" in error for error in self.errors())
        )

    def test_root_must_route_every_specialty_agent(self) -> None:
        (self.root / "AGENTS.md").write_text(
            "## Mandatory delivery flow\n"
            ".github/governance/agent-governance.json\n"
            "agents/review.md\n",
            encoding="utf-8",
        )
        self.assertIn(
            "root policy does not route specialty agent: agents/testing.md",
            self.errors(),
        )

    def test_unreferenced_method_skill_is_rejected(self) -> None:
        profile = self.root / ".openhands/skills/repo.md"
        profile.write_text(
            "## Rule loading\n"
            ".github/governance/agent-governance.json\n"
            "agents/review.md\nagents/testing.md\n",
            encoding="utf-8",
        )
        (self.root / "agents/testing.md").write_text("Evidence Matrix\n", encoding="utf-8")
        self.assertIn(
            "method skill is not referenced by any agent/profile: "
            ".openhands/skills/fault-injection.md",
            self.errors(),
        )


    def test_specialty_agent_must_live_under_agents_directory(self) -> None:
        self.manifest["specialty_agents"] = ["agents.review.md", "agents/testing.md"]
        (self.root / ".github/governance/agent-governance.json").write_text(
            json.dumps(self.manifest), encoding="utf-8"
        )
        self.assertTrue(
            any("must live directly under agents/" in error for error in self.errors())
        )

    def test_legacy_root_level_agent_file_is_rejected(self) -> None:
        (self.root / "agents.legacy.md").write_text("# legacy\n", encoding="utf-8")
        self.assertIn(
            "legacy root-level specialty agent is forbidden: agents.legacy.md",
            self.errors(),
        )


if __name__ == "__main__":
    unittest.main()
