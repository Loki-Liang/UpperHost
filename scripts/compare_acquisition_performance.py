#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def compare(baseline: dict, current: dict) -> tuple[str, list[str]]:
    if baseline.get("status") != "Active":
        return "NotComparable", [f"baseline status is {baseline.get('status')!r}"]
    if baseline.get("environmentId") != current.get("environmentId"):
        return "NotComparable", [
            f"environment mismatch: baseline={baseline.get('environmentId')!r}, current={current.get('environmentId')!r}"
        ]
    if baseline.get("environmentFingerprintSha256") != current.get("environmentFingerprintSha256"):
        return "NotComparable", [
            "environment fingerprint mismatch; CPU/OS/.NET runner state is not comparable to the reviewed baseline"
        ]

    policy = baseline["policy"]
    mean_limit = 1 + policy["maxMeanRegressionPercent"] / 100.0
    allocation_limit = 1 + policy["maxAllocationRegressionPercent"] / 100.0
    min_iterations = policy["minimumMeasuredIterations"]
    failures: list[str] = []

    for name, expected in baseline["benchmarks"].items():
        actual = current.get("benchmarks", {}).get(name)
        if actual is None:
            failures.append(f"{name}: current measurement missing")
            continue
        if actual.get("measuredIterations", 0) < min_iterations:
            failures.append(
                f"{name}: measuredIterations={actual.get('measuredIterations')} < {min_iterations}"
            )
        expected_mean = expected.get("meanNs")
        actual_mean = actual.get("meanNs")
        if isinstance(expected_mean, (int, float)) and isinstance(actual_mean, (int, float)):
            if actual_mean > expected_mean * mean_limit:
                failures.append(
                    f"{name}: mean {actual_mean:.3f}ns exceeds baseline {expected_mean:.3f}ns by policy"
                )
        else:
            failures.append(f"{name}: mean measurement missing")

        expected_alloc = expected.get("allocatedBytesPerOperation")
        actual_alloc = actual.get("allocatedBytesPerOperation")
        if isinstance(expected_alloc, (int, float)):
            if not isinstance(actual_alloc, (int, float)):
                failures.append(f"{name}: allocation measurement missing")
            elif actual_alloc > expected_alloc * allocation_limit:
                failures.append(
                    f"{name}: allocation {actual_alloc:.3f} exceeds baseline {expected_alloc:.3f} by policy"
                )

    return ("Failed" if failures else "Passed"), failures


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--current", required=True)
    args = parser.parse_args()

    baseline = json.loads(Path(args.baseline).read_text(encoding="utf-8"))
    current = json.loads(Path(args.current).read_text(encoding="utf-8"))
    status, messages = compare(baseline, current)
    print(f"Acquisition performance comparison: {status}")
    for message in messages:
        print(f"- {message}", file=sys.stderr if status != "Passed" else sys.stdout)
    return 0 if status == "Passed" else (2 if status == "NotComparable" else 1)


if __name__ == "__main__":
    raise SystemExit(main())
