#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
CONTRACT_PATH = ROOT / "eng" / "compatibility" / "source-scaffold-contract.json"


def fail(errors: list[str]) -> None:
    print("Source-scaffold compatibility contract failed:", file=sys.stderr)
    for error in errors:
        print(f" - {error}", file=sys.stderr)
    raise SystemExit(1)


def nested_value(document: object, path: str) -> object:
    current = document
    for segment in path.split(":"):
        if not isinstance(current, dict) or segment not in current:
            raise KeyError(path)
        current = current[segment]
    return current


def normalize_reference(value: str) -> str:
    return str(PurePosixPath(value.replace("\\", "/")))


def main() -> None:
    contract = json.loads(CONTRACT_PATH.read_text(encoding="utf-8"))
    errors: list[str] = []

    project_path = ROOT / contract["appProject"]
    if not project_path.is_file():
        fail([f"canonical app project is missing: {contract['appProject']}"])

    root = ET.parse(project_path).getroot()

    def property_value(name: str) -> str | None:
        node = root.find(f".//{name}")
        return node.text.strip() if node is not None and node.text else None

    expected_properties = {
        "TargetFramework": contract["targetFramework"],
        "OutputType": contract["outputType"],
        "UseWPF": str(contract["useWpf"]).lower(),
    }
    for name, expected in expected_properties.items():
        actual = property_value(name)
        if name == "UseWPF" and actual is not None:
            actual = actual.lower()
        if actual != str(expected):
            errors.append(f"{name} expected {expected!r}, got {actual!r}")

    actual_refs = {
        normalize_reference(node.attrib["Include"])
        for node in root.findall(".//ProjectReference")
        if "Include" in node.attrib
    }
    for required in contract["requiredProjectReferences"]:
        normalized = normalize_reference(required)
        if normalized not in actual_refs:
            errors.append(f"required ProjectReference is missing: {required}")

    if contract.get("forbidPackageReferencesInApp", False):
        package_refs = [
            node.attrib.get("Include", "<unknown>")
            for node in root.findall(".//PackageReference")
        ]
        if package_refs:
            errors.append(
                "canonical app must remain source-first and cannot replace ProjectReferences "
                f"with PackageReferences: {', '.join(package_refs)}"
            )

    startup_path = ROOT / contract["startupFile"]
    if not startup_path.is_file():
        errors.append(f"startup file is missing: {contract['startupFile']}")
    else:
        startup = startup_path.read_text(encoding="utf-8")
        for token in contract["requiredStartupTokens"]:
            if token not in startup:
                errors.append(f"startup contract token is missing: {token}")

    config_path = ROOT / contract["configurationFile"]
    if not config_path.is_file():
        errors.append(f"configuration file is missing: {contract['configurationFile']}")
    else:
        config = json.loads(config_path.read_text(encoding="utf-8"))
        for key_path in contract["requiredConfigurationPaths"]:
            try:
                nested_value(config, key_path)
            except KeyError:
                errors.append(f"required configuration path is missing: {key_path}")

        try:
            transport_type = str(nested_value(config, "OpenDeviceStudio:Transport:Type")).lower()
            expected_type = str(contract["defaultTransportType"]).lower()
            if transport_type != expected_type:
                errors.append(
                    f"default transport expected {expected_type!r}, got {transport_type!r}"
                )
        except KeyError:
            pass

    if errors:
        fail(errors)

    print(
        "Source-scaffold compatibility contract passed: "
        f"{contract['appProject']} + startup + configuration."
    )


if __name__ == "__main__":
    main()
