#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not available; run .openhands/setup.sh first." >&2
  exit 1
fi

python scripts/validate_agent_governance.py
python scripts/validate_architecture.py
python scripts/validate_source_scaffold_contract.py
python scripts/validate_acquisition_verification.py
dotnet build OpenDeviceStudio.slnx -c Release -p:EnableWindowsTargeting=true
dotnet test tests/OpenDeviceStudio.Tests/OpenDeviceStudio.Tests.csproj -c Release --no-build
