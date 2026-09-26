#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROFILE_ROOT = ROOT / "eng" / "acquisition-verification" / "profiles"

REQUIRED_PROFILES = {"small-reference.json", "typical-multichannel.json", "stress.json"}
TOP_LEVEL = {
    "schemaVersion", "id", "evidenceKind", "mode", "sourceCount",
    "channelsPerSource", "numericType", "bytesPerSample", "sampleRateHz",
    "blockSizeSamples", "durationSeconds", "expectedRawBytesPerSecond",
    "rawRecorder", "routerBranches", "pipelineStages", "presentation", "faultSchedule",
}
RAW_KEYS = {"queueCapacityBlocks", "durability"}
BRANCH_KEYS = {"id", "delivery", "capacityBlocks", "overflow"}
PRESENTATION_KEYS = {"viewportSamples", "targetFps"}
FAULT_KEYS = {"target", "kind", "atSequence", "durationMs"}
MODES = {"VirtualTimeDeterministic", "WallClockPaced", "MaxThroughput"}
EVIDENCE_KINDS = {"Synthetic", "Loopback", "Hardware"}
NUMERIC_WIDTHS = {"int16": 2, "int32": 4, "float32": 4, "float64": 8}
DURABILITY = {"Buffered", "FlushOnFinalize", "FlushToDiskOnFinalize"}
DELIVERIES = {"Required", "Optional"}
OVERFLOW = {"Wait", "Reject", "DropOldest", "DropNewest", "Latest"}
LOSSY = {"DropOldest", "DropNewest", "Latest"}
FAULT_TARGETS = {
    "RawRecorder", "Processing", "RouterRequired", "RouterOptional",
    "Presentation", "Provider", "Parser",
}
FAULT_KINDS = {
    "Delay", "Throw", "DiskFull", "WriteFailure", "FlushFailure",
    "Disconnect", "Corrupt", "Cancel",
}


class ValidationError(ValueError):
    pass


def _exact_keys(value: dict, expected: set[str], context: str) -> None:
    unknown = set(value) - expected
    missing = expected - set(value)
    if unknown:
        raise ValidationError(f"{context}: unknown field(s): {sorted(unknown)}")
    if missing:
        raise ValidationError(f"{context}: missing field(s): {sorted(missing)}")


def _positive_int(value: object, context: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValidationError(f"{context}: expected positive integer, got {value!r}")
    return value


def validate_profile(profile: dict, filename: str) -> None:
    if not isinstance(profile, dict):
        raise ValidationError(f"{filename}: root must be an object")
    _exact_keys(profile, TOP_LEVEL, filename)

    if profile["schemaVersion"] != 1:
        raise ValidationError(f"{filename}: unsupported schemaVersion {profile['schemaVersion']!r}")
    if not isinstance(profile["id"], str) or not profile["id"].strip():
        raise ValidationError(f"{filename}: id is required")
    if profile["mode"] not in MODES:
        raise ValidationError(f"{filename}: invalid mode {profile['mode']!r}")
    if profile["evidenceKind"] not in EVIDENCE_KINDS:
        raise ValidationError(f"{filename}: invalid evidenceKind {profile['evidenceKind']!r}")

    source_count = _positive_int(profile["sourceCount"], f"{filename}.sourceCount")
    channels = _positive_int(profile["channelsPerSource"], f"{filename}.channelsPerSource")
    sample_rate = _positive_int(profile["sampleRateHz"], f"{filename}.sampleRateHz")
    _positive_int(profile["blockSizeSamples"], f"{filename}.blockSizeSamples")
    _positive_int(profile["durationSeconds"], f"{filename}.durationSeconds")

    numeric_type = profile["numericType"]
    if numeric_type not in NUMERIC_WIDTHS:
        raise ValidationError(f"{filename}: invalid numericType {numeric_type!r}")
    width = _positive_int(profile["bytesPerSample"], f"{filename}.bytesPerSample")
    if NUMERIC_WIDTHS[numeric_type] != width:
        raise ValidationError(
            f"{filename}: numericType {numeric_type!r} requires {NUMERIC_WIDTHS[numeric_type]} bytes, got {width}"
        )

    expected_rate = source_count * channels * width * sample_rate
    if profile["expectedRawBytesPerSecond"] != expected_rate:
        raise ValidationError(
            f"{filename}: expectedRawBytesPerSecond={profile['expectedRawBytesPerSecond']!r}; calculated={expected_rate}"
        )

    raw = profile["rawRecorder"]
    if not isinstance(raw, dict):
        raise ValidationError(f"{filename}: rawRecorder must be an object")
    _exact_keys(raw, RAW_KEYS, f"{filename}.rawRecorder")
    _positive_int(raw["queueCapacityBlocks"], f"{filename}.rawRecorder.queueCapacityBlocks")
    if raw["durability"] not in DURABILITY:
        raise ValidationError(f"{filename}: invalid rawRecorder durability {raw['durability']!r}")

    branches = profile["routerBranches"]
    if not isinstance(branches, list) or not branches:
        raise ValidationError(f"{filename}: routerBranches must be non-empty")
    branch_ids: set[str] = set()
    required_seen = False
    optional_seen = False
    for index, branch in enumerate(branches):
        context = f"{filename}.routerBranches[{index}]"
        if not isinstance(branch, dict):
            raise ValidationError(f"{context}: branch must be an object")
        _exact_keys(branch, BRANCH_KEYS, context)
        branch_id = branch["id"]
        if not isinstance(branch_id, str) or not branch_id.strip() or branch_id in branch_ids:
            raise ValidationError(f"{context}: branch id must be non-empty and unique")
        if branch_id.lower() in {"raw", "raw-recorder", "rawrecorder"}:
            raise ValidationError(f"{context}: Raw Recorder is independent and must not be modeled as a Router branch")
        branch_ids.add(branch_id)
        delivery = branch["delivery"]
        overflow = branch["overflow"]
        if delivery not in DELIVERIES:
            raise ValidationError(f"{context}: invalid delivery {delivery!r}")
        if overflow not in OVERFLOW:
            raise ValidationError(f"{context}: invalid overflow {overflow!r}")
        _positive_int(branch["capacityBlocks"], f"{context}.capacityBlocks")
        if delivery == "Required":
            required_seen = True
            if overflow in LOSSY:
                raise ValidationError(f"{context}: Required branch cannot use lossy overflow {overflow}")
        else:
            optional_seen = True
            if overflow == "Wait":
                raise ValidationError(f"{context}: Optional branch cannot use Wait and backpressure Required work")
    if not required_seen or not optional_seen:
        raise ValidationError(f"{filename}: profiles must exercise both Required and Optional Router branches")

    stages = profile["pipelineStages"]
    if not isinstance(stages, list) or not stages or any(not isinstance(x, str) or not x.strip() for x in stages):
        raise ValidationError(f"{filename}: pipelineStages must contain non-empty names")

    presentation = profile["presentation"]
    if not isinstance(presentation, dict):
        raise ValidationError(f"{filename}: presentation must be an object")
    _exact_keys(presentation, PRESENTATION_KEYS, f"{filename}.presentation")
    _positive_int(presentation["viewportSamples"], f"{filename}.presentation.viewportSamples")
    _positive_int(presentation["targetFps"], f"{filename}.presentation.targetFps")

    faults = profile["faultSchedule"]
    if not isinstance(faults, list):
        raise ValidationError(f"{filename}: faultSchedule must be an array")
    for index, fault in enumerate(faults):
        context = f"{filename}.faultSchedule[{index}]"
        if not isinstance(fault, dict):
            raise ValidationError(f"{context}: fault must be an object")
        allowed = FAULT_KEYS if "durationMs" in fault else FAULT_KEYS - {"durationMs"}
        _exact_keys(fault, allowed, context)
        if fault["target"] not in FAULT_TARGETS:
            raise ValidationError(f"{context}: invalid target {fault['target']!r}")
        if fault["kind"] not in FAULT_KINDS:
            raise ValidationError(f"{context}: invalid kind {fault['kind']!r}")
        _positive_int(fault["atSequence"], f"{context}.atSequence")
        if "durationMs" in fault:
            duration = fault["durationMs"]
            if not isinstance(duration, int) or isinstance(duration, bool) or duration < 0:
                raise ValidationError(f"{context}.durationMs: expected non-negative integer")


def validate_repository(profile_root: Path = PROFILE_ROOT) -> None:
    if not profile_root.is_dir():
        raise ValidationError(f"missing profile directory: {profile_root}")

    files = {p.name for p in profile_root.glob("*.json")}
    missing = REQUIRED_PROFILES - files
    if missing:
        raise ValidationError(f"missing required profile(s): {sorted(missing)}")

    seen_ids: set[str] = set()
    fault_targets: set[str] = set()
    for path in sorted(profile_root.glob("*.json")):
        profile = json.loads(path.read_text(encoding="utf-8"))
        validate_profile(profile, path.name)
        if profile["id"] in seen_ids:
            raise ValidationError(f"duplicate profile id: {profile['id']}")
        seen_ids.add(profile["id"])
        fault_targets.update(f["target"] for f in profile["faultSchedule"])

    missing_fault_targets = {"RawRecorder", "Processing", "Presentation"} - fault_targets
    if missing_fault_targets:
        raise ValidationError(
            "fault campaign must include independent slow/fault coverage for "
            f"{sorted(missing_fault_targets)}"
        )


def main() -> int:
    try:
        validate_repository()
    except (ValidationError, json.JSONDecodeError) as exc:
        print(f"Acquisition verification profile validation failed: {exc}", file=sys.stderr)
        return 1
    print("Acquisition verification profiles: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
