import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "validate_acquisition_verification.py"
SPEC = importlib.util.spec_from_file_location("acq_verify", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

def valid_profile():
    return {
        "schemaVersion": 1, "id": "test-v1", "evidenceKind": "Synthetic",
        "mode": "VirtualTimeDeterministic", "sourceCount": 1, "channelsPerSource": 2,
        "numericType": "float32", "bytesPerSample": 4, "sampleRateHz": 1000,
        "blockSizeSamples": 100, "durationSeconds": 10,
        "expectedRawBytesPerSecond": 8000,
        "rawRecorder": {"queueCapacityBlocks": 8, "durability": "FlushOnFinalize"},
        "routerBranches": [
            {"id": "processing", "delivery": "Required", "capacityBlocks": 8, "overflow": "Wait"},
            {"id": "presentation", "delivery": "Optional", "capacityBlocks": 2, "overflow": "DropOldest"},
        ],
        "pipelineStages": ["identity"],
        "presentation": {"viewportSamples": 1000, "targetFps": 30},
        "faultSchedule": [],
    }

class AcquisitionVerificationValidatorTests(unittest.TestCase):
    def test_valid_profile_is_accepted(self):
        MODULE.validate_profile(valid_profile(), "test.json")

    def test_unknown_field_is_rejected(self):
        profile = valid_profile(); profile["magic"] = True
        with self.assertRaises(MODULE.ValidationError):
            MODULE.validate_profile(profile, "test.json")

    def test_expected_rate_is_calculated_from_sources_channels_width_and_rate(self):
        profile = valid_profile(); profile["sourceCount"] = 2
        with self.assertRaises(MODULE.ValidationError):
            MODULE.validate_profile(profile, "test.json")

    def test_raw_recorder_is_not_a_router_branch(self):
        profile = valid_profile()
        profile["routerBranches"][0]["id"] = "raw"
        with self.assertRaises(MODULE.ValidationError):
            MODULE.validate_profile(profile, "test.json")

    def test_required_branch_cannot_be_lossy(self):
        profile = valid_profile(); profile["routerBranches"][0]["overflow"] = "DropOldest"
        with self.assertRaises(MODULE.ValidationError):
            MODULE.validate_profile(profile, "test.json")

    def test_optional_branch_cannot_backpressure_required_work(self):
        profile = valid_profile(); profile["routerBranches"][1]["overflow"] = "Wait"
        with self.assertRaises(MODULE.ValidationError):
            MODULE.validate_profile(profile, "test.json")

    def test_duplicate_fault_schedule_is_rejected(self):
        profile = valid_profile()
        fault = {"target": "Processing", "kind": "Delay", "atSequence": 10, "durationMs": 1}
        profile["faultSchedule"] = [fault, dict(fault)]
        with self.assertRaises(MODULE.ValidationError):
            MODULE.validate_profile(profile, "test.json")

    def test_repository_requires_three_capacity_profiles_and_fault_classes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            profiles = {
                "small-reference.json": valid_profile(),
                "typical-multichannel.json": copy.deepcopy(valid_profile()),
                "stress.json": copy.deepcopy(valid_profile()),
            }
            profiles["small-reference.json"]["id"] = "small"
            profiles["typical-multichannel.json"]["id"] = "typical"
            profiles["stress.json"]["id"] = "stress"
            profiles["stress.json"]["faultSchedule"] = [
                {"target": "RawRecorder", "kind": "Delay", "atSequence": 10, "durationMs": 10},
                {"target": "Processing", "kind": "Delay", "atSequence": 20, "durationMs": 10},
                {"target": "Presentation", "kind": "Delay", "atSequence": 30, "durationMs": 10},
            ]
            for name, profile in profiles.items():
                (root / name).write_text(json.dumps(profile), encoding="utf-8")
            MODULE.validate_repository(root)

if __name__ == "__main__":
    unittest.main()
