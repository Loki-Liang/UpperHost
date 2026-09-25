#!/usr/bin/env python3
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOTS = ("src", "app", "samples", "tests")


def project_files() -> list[Path]:
    files: list[Path] = []
    for root_name in PROJECT_ROOTS:
        root = ROOT / root_name
        if root.exists():
            files.extend(root.rglob("*.csproj"))
    return sorted(path.resolve() for path in files)


def references(project: Path) -> list[Path]:
    tree = ET.parse(project)
    refs: list[Path] = []
    for node in tree.findall(".//ProjectReference"):
        include = node.attrib.get("Include")
        if not include:
            continue
        target = (project.parent / include).resolve()
        refs.append(target)
    return refs


projects = project_files()
project_set = set(projects)
graph = {project: [ref for ref in references(project) if ref in project_set] for project in projects}
errors: list[str] = []


def rel(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def name(path: Path) -> str:
    return path.stem


# Rule 1: repository project references must be acyclic.
state: dict[Path, int] = {}
stack: list[Path] = []


def visit(node: Path) -> None:
    status = state.get(node, 0)
    if status == 2:
        return
    if status == 1:
        try:
            index = stack.index(node)
            cycle = stack[index:] + [node]
        except ValueError:
            cycle = stack + [node]
        errors.append("ProjectReference cycle: " + " -> ".join(name(p) for p in cycle))
        return

    state[node] = 1
    stack.append(node)
    for target in graph[node]:
        visit(target)
    stack.pop()
    state[node] = 2


for project in projects:
    visit(project)


# Rule 2: Abstractions is the dependency root and references no repository project.
for project in projects:
    if name(project) == "UpperHost.Abstractions" and graph[project]:
        errors.append(
            "UpperHost.Abstractions must not reference repository projects: "
            + ", ".join(name(p) for p in graph[project])
        )


# Rule 3: reusable production modules never depend on the product app, samples, or tests.
for project in projects:
    project_rel = rel(project)
    if not project_rel.startswith("src/"):
        continue
    for target in graph[project]:
        target_rel = rel(target)
        if target_rel.startswith(("app/", "samples/", "tests/")):
            errors.append(f"{project_rel} must not depend on {target_rel}")


# Rule 4: Starters is a composition module; production modules must not depend back on it.
for project in projects:
    if not rel(project).startswith("src/") or name(project) == "UpperHost.Starters":
        continue
    for target in graph[project]:
        if name(target) == "UpperHost.Starters":
            errors.append(f"{rel(project)} must not depend on UpperHost.Starters")


# Rule 5: Presentation is an outer adapter. No non-presentation production module may depend on it.
for project in projects:
    project_name = name(project)
    if not rel(project).startswith("src/") or project_name.startswith("UpperHost.Presentation."):
        continue
    for target in graph[project]:
        if name(target).startswith("UpperHost.Presentation."):
            errors.append(f"{rel(project)} must not depend on presentation project {rel(target)}")


# Rule 6: Presentation adapters may depend only on stable contracts/hosting.
for project in projects:
    if not name(project).startswith("UpperHost.Presentation."):
        continue
    allowed = {"UpperHost.Abstractions", "UpperHost.Hosting"}
    for target in graph[project]:
        if name(target) not in allowed:
            errors.append(
                f"{rel(project)} presentation dependency {name(target)} is not allowed; "
                f"allowed: {', '.join(sorted(allowed))}"
            )


if errors:
    print("Architecture validation failed:")
    for error in errors:
        print(f"  - {error}")
    sys.exit(1)

print(f"Architecture validation passed for {len(projects)} projects.")
print("No ProjectReference cycles or forbidden modular-monolith dependency directions found.")
