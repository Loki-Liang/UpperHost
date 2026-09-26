from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEGACY_BRAND = "Upper" + "Host"
ALLOWED_LEGACY_CONTENT = {
    "eng/compatibility/package-renames.json",
    "docs/migrations/0.2.0-opendevicestudio-rename.md",
    "docs/migrations/0.2.0-opendevicestudio-rename.zh-CN.md",
}
TEXT_SUFFIXES = {
    ".cs",
    ".csproj",
    ".slnx",
    ".xaml",
    ".json",
    ".yml",
    ".yaml",
    ".md",
    ".ps1",
    ".py",
    ".sh",
    ".props",
}
TEXT_NAMES = {".gitignore"}


def is_text_file(path: Path) -> bool:
    return path.suffix.lower() in TEXT_SUFFIXES or path.name in TEXT_NAMES


def main() -> int:
    failures: list[str] = []
    legacy_lower = LEGACY_BRAND.lower()

    for path in ROOT.rglob("*"):
        if not path.is_file() or ".git" in path.parts:
            continue

        relative = path.relative_to(ROOT).as_posix()

        if legacy_lower in relative.lower():
            failures.append(f"legacy brand remains in path: {relative}")

        if relative in ALLOWED_LEGACY_CONTENT or not is_text_file(path):
            continue

        try:
            content = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue

        if legacy_lower in content.lower():
            failures.append(f"legacy brand remains in active content: {relative}")

    if failures:
        print("OpenDeviceStudio brand migration closure failed:")
        for failure in failures:
            print(f" - {failure}")
        return 1

    print("OpenDeviceStudio brand migration closure passed: no legacy brand remains outside approved migration evidence.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
