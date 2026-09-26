import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "validate_acquisition_release_readiness.py"
SPEC = importlib.util.spec_from_file_location("acq_readiness", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


def coverage(status="Production"):
    return {
        "schemaVersion": 1,
        "components": {
            "rawRecorder": {
                "status": status,
                "issue": 58,
                "evidence": ["tests/evidence.cs"] if status == "Production" else [],
                "note": "test",
            }
        },
        "requiredForIssueClosure": ["rawRecorder"],
    }


def baseline(status="Active"):
    return {
        "schemaVersion": 1,
        "status": status,
        "environmentId": "stable-v1",
        "sourceSha": "abc" if status == "Active" else None,
        "updatedAtUtc": "2026-09-27T00:00:00Z" if status == "Active" else None,
        "environmentFingerprintSha256": "f" * 64 if status == "Active" else None,
        "policy": {
            "maxMeanRegressionPercent": 10,
            "maxAllocationRegressionPercent": 10,
            "minimumMeasuredIterations": 8,
            "requiresExplicitReviewForBaselineChange": True,
        },
        "benchmarks": {"A": {"meanNs": 1}} if status == "Active" else {},
    }


class AcquisitionReleaseReadinessTests(unittest.TestCase):
    def test_ready_when_required_components_and_baseline_are_active(self):
        c = coverage(); b = baseline()
        MODULE.validate_coverage(c); MODULE.validate_baseline(b)
        self.assertEqual([], MODULE.release_blockers(c, b))

    def test_blocked_component_is_machine_visible(self):
        c = coverage("Blocked"); b = baseline()
        MODULE.validate_coverage(c); MODULE.validate_baseline(b)
        self.assertIn("rawRecorder: Blocked (issue #58)", MODULE.release_blockers(c, b))

    def test_uninitialized_baseline_is_not_comparable(self):
        c = coverage(); b = baseline("Uninitialized")
        MODULE.validate_coverage(c); MODULE.validate_baseline(b)
        self.assertTrue(any("Uninitialized" in item for item in MODULE.release_blockers(c, b)))

    def test_production_component_without_evidence_is_rejected(self):
        c = coverage(); c["components"]["rawRecorder"]["evidence"] = []
        with self.assertRaises(MODULE.ValidationError):
            MODULE.validate_coverage(c)


if __name__ == "__main__":
    unittest.main()
