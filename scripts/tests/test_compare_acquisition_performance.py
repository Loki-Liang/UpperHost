import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / filename)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module

COMPARE = load("acq_compare", "compare_acquisition_performance.py")

class AcquisitionPerformanceComparisonTests(unittest.TestCase):
    def baseline(self):
        return {
            "status": "Active",
            "environmentId": "stable",
            "policy": {
                "maxMeanRegressionPercent": 10,
                "maxAllocationRegressionPercent": 10,
                "minimumMeasuredIterations": 8,
            },
            "benchmarks": {"A": {"meanNs": 100, "allocatedBytesPerOperation": 50}},
        }

    def current(self):
        return {
            "environmentId": "stable",
            "benchmarks": {
                "A": {"meanNs": 105, "allocatedBytesPerOperation": 52, "measuredIterations": 8}
            },
        }

    def test_passes_inside_thresholds(self):
        self.assertEqual(("Passed", []), COMPARE.compare(self.baseline(), self.current()))

    def test_fails_regression(self):
        current = self.current()
        current["benchmarks"]["A"]["meanNs"] = 111
        status, messages = COMPARE.compare(self.baseline(), current)
        self.assertEqual("Failed", status)
        self.assertTrue(any("mean" in item for item in messages))

    def test_uninitialized_is_not_comparable(self):
        baseline = self.baseline()
        baseline["status"] = "Uninitialized"
        status, _ = COMPARE.compare(baseline, self.current())
        self.assertEqual("NotComparable", status)

    def test_environment_mismatch_is_not_comparable(self):
        current = self.current()
        current["environmentId"] = "other"
        status, _ = COMPARE.compare(self.baseline(), current)
        self.assertEqual("NotComparable", status)

if __name__ == "__main__":
    unittest.main()
