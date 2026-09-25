#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not available; run .openhands/setup.sh first." >&2
  exit 1
fi

python scripts/validate_architecture.py
python scripts/validate_source_scaffold_contract.py
dotnet build UpperHost.slnx -c Release -p:EnableWindowsTargeting=true
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release --no-build
