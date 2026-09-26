#!/usr/bin/env python3
"""Validate UpperHost layered AI/OpenHands governance.

The validator intentionally uses only the Python standard library so the same
contract runs in OpenHands and GitHub Actions.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = Path(".github/governance/agent-governance.json")


def _read(root: Path, relative: str, errors: list[str]) -> str:
    path = root / relative
    if not path.is_file():
        errors.append(f"missing governance file: {relative}")
        return ""
    text = path.read_text(encoding="utf-8")
    if not text.strip():
        errors.append(f"empty governance file: {relative}")
    return text


def validate_governance(root: Path = ROOT) -> list[str]:
    errors: list[str] = []
    manifest_path = root / MANIFEST
    if not manifest_path.is_file():
        return [f"missing governance manifest: {MANIFEST.as_posix()}"]

    try:
        policy = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        return [f"invalid governance manifest: {error}"]

    if policy.get("version") != 1:
        errors.append("agent-governance manifest version must be 1")

    root_policy = policy.get("root_policy")
    localized_root = policy.get("localized_root_policy")
    profile = policy.get("openhands_profile")
    agents = policy.get("specialty_agents")
    skills = policy.get("method_skills")

    if not isinstance(root_policy, str) or not root_policy:
        errors.append("root_policy must be a non-empty string")
        root_policy = "AGENTS.md"
    if not isinstance(localized_root, str) or not localized_root:
        errors.append("localized_root_policy must be a non-empty string")
        localized_root = "AGENTS.zh-CN.md"
    if not isinstance(profile, str) or not profile:
        errors.append("openhands_profile must be a non-empty string")
        profile = ".openhands/skills/repo.md"
    if not isinstance(agents, list) or not agents or not all(isinstance(item, str) for item in agents):
        errors.append("specialty_agents must be a non-empty string list")
        agents = []
    if not isinstance(skills, list) or not skills or not all(isinstance(item, str) for item in skills):
        errors.append("method_skills must be a non-empty string list")
        skills = []

    if len(agents) != len(set(agents)):
        errors.append("specialty_agents contains duplicate paths")
    if len(skills) != len(set(skills)):
        errors.append("method_skills contains duplicate paths")

    for relative in agents:
        if not relative.startswith("agents/") or Path(relative).parent != Path("agents"):
            errors.append(f"specialty agent must live directly under agents/: {relative}")

    root_level_specialty = sorted(root.glob("agents.*.md"))
    for path in root_level_specialty:
        errors.append(f"legacy root-level specialty agent is forbidden: {path.name}")

    root_text = _read(root, root_policy, errors)
    localized_text = _read(root, localized_root, errors)
    profile_text = _read(root, profile, errors)

    for marker in policy.get("root_required_markers", []):
        if marker not in root_text:
            errors.append(f"root policy missing required marker: {marker}")
    for marker in policy.get("localized_root_required_markers", []):
        if marker not in localized_text:
            errors.append(f"localized root policy missing required marker: {marker}")
    for marker in policy.get("profile_required_markers", []):
        if marker not in profile_text:
            errors.append(f"OpenHands profile missing required marker: {marker}")

    agent_texts: dict[str, str] = {}
    for relative in agents:
        text = _read(root, relative, errors)
        agent_texts[relative] = text
        if relative not in root_text:
            errors.append(f"root policy does not route specialty agent: {relative}")
        if relative not in localized_text:
            errors.append(f"localized root policy does not route specialty agent: {relative}")
        if relative not in profile_text:
            errors.append(f"OpenHands profile does not route specialty agent: {relative}")

    combined_method_references = profile_text + "\n" + "\n".join(agent_texts.values())
    for relative in skills:
        _read(root, relative, errors)
        if relative not in combined_method_references:
            errors.append(f"method skill is not referenced by any agent/profile: {relative}")

    if MANIFEST.as_posix() not in root_text:
        errors.append("root policy must reference the governance manifest")
    if MANIFEST.as_posix() not in profile_text:
        errors.append("OpenHands profile must reference the governance manifest")
    if "agents/review.md" in agents and "Five-Gate Review" not in agent_texts.get("agents/review.md", ""):
        errors.append("agents/review.md must define the Five-Gate Review")
    if "agents/testing.md" in agents and "Evidence Matrix" not in agent_texts.get("agents/testing.md", ""):
        errors.append("agents/testing.md must define the Evidence Matrix")

    return errors


def main() -> None:
    errors = validate_governance()
    if errors:
        print("agent-governance validation failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        raise SystemExit(1)

    manifest = json.loads((ROOT / MANIFEST).read_text(encoding="utf-8"))
    print("agent-governance validation passed")
    print(f"specialty agents: {len(manifest['specialty_agents'])}")
    print(f"method skills: {len(manifest['method_skills'])}")


if __name__ == "__main__":
    main()
