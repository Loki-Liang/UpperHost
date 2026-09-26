#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def _number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def fingerprint_sha256(path: Path) -> str:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    canonical = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def normalize_report(root: Path, environment_id: str, source_sha: str, environment_fingerprint: Path) -> dict:
    benchmarks: dict[str, dict] = {}
    files = sorted(root.rglob("*-report-full.json"))
    if not files:
        raise ValueError(f"No BenchmarkDotNet *-report-full.json found under {root}")

    for path in files:
        report = json.loads(path.read_text(encoding="utf-8-sig"))
        for item in report.get("Benchmarks", []):
            name = item.get("DisplayInfo") or item.get("FullName") or item.get("Method")
            if not isinstance(name, str) or not name:
                raise ValueError(f"{path}: benchmark name missing")
            statistics = item.get("Statistics") or {}
            mean = _number(statistics.get("Mean"))
            if mean is None:
                raise ValueError(f"{path}: {name}: mean missing")
            iterations = statistics.get("N")
            if not isinstance(iterations, int):
                values = statistics.get("OriginalValues")
                iterations = len(values) if isinstance(values, list) else 0

            memory = item.get("Memory") or item.get("GcStats") or {}
            allocation = None
            for key in ("BytesAllocatedPerOperation", "AllocatedBytesPerOperation", "BytesAllocatedPerOp"):
                allocation = _number(memory.get(key))
                if allocation is not None:
                    break

            benchmarks[name] = {
                "meanNs": mean,
                "allocatedBytesPerOperation": allocation,
                "measuredIterations": iterations,
                "sourceReport": str(path).replace("\\", "/"),
            }

    return {
        "schemaVersion": 1,
        "environmentId": environment_id,
        "sourceSha": source_sha,
        "environmentFingerprintSha256": fingerprint_sha256(environment_fingerprint),
        "benchmarks": benchmarks,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifacts", required=True)
    parser.add_argument("--environment-id", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--environment-fingerprint", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    normalized = normalize_report(
        Path(args.artifacts),
        args.environment_id,
        args.source_sha,
        Path(args.environment_fingerprint),
    )
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(normalized, indent=2) + "\n", encoding="utf-8")
    print(f"Normalized {len(normalized['benchmarks'])} benchmark(s) -> {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
