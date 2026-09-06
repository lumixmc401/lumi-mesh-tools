#!/usr/bin/env python3
"""Sanity checks for package.json, run on every push and again at release time."""
import argparse
import json
import pathlib
import re
import sys

SEMVER = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")
REQUIRED = ["name", "displayName", "version", "unity", "description", "author"]

root = pathlib.Path(__file__).resolve().parents[2]
errors = []


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--expect-version", help="version the release tag claims")
    args = parser.parse_args()

    try:
        package = json.loads((root / "package.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"package.json could not be read: {exc}")
        return 1

    for field in REQUIRED:
        if not package.get(field):
            errors.append(f"package.json is missing '{field}'")

    version = package.get("version", "")
    if version and not SEMVER.match(version):
        errors.append(f"version '{version}' is not semantic versioning")

    if not re.match(r"^[a-z0-9._-]+$", package.get("name", "")):
        errors.append("name must be a lowercase reverse-domain id")

    if args.expect_version and args.expect_version != version:
        errors.append(
            f"tag says {args.expect_version} but package.json says {version}"
        )

    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8")
    if version and f"[{version}]" not in changelog:
        errors.append(f"CHANGELOG.md has no '[{version}]' section")

    asmdefs = list(root.glob("**/*.asmdef"))
    if not asmdefs:
        errors.append("no assembly definition found")

    for asmdef in asmdefs:
        try:
            json.loads(asmdef.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            errors.append(f"{asmdef.relative_to(root)} is not valid JSON: {exc}")

    for error in errors:
        print(f"error: {error}")
    if errors:
        return 1

    print(f"{package['name']} {version} looks fine")
    return 0


if __name__ == "__main__":
    sys.exit(main())
