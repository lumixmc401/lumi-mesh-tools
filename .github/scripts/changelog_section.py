#!/usr/bin/env python3
"""Prints the CHANGELOG section for a version, for use as release notes."""
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parents[2]
version = sys.argv[1] if len(sys.argv) > 1 else json.loads(
    (root / "package.json").read_text(encoding="utf-8"))["version"]

text = (root / "CHANGELOG.md").read_text(encoding="utf-8")
pattern = re.compile(
    r"^##\s*\[" + re.escape(version) + r"\].*?$(.*?)(?=^##\s|\Z)",
    re.MULTILINE | re.DOTALL,
)
match = pattern.search(text)
print(match.group(1).strip() if match else f"Release {version}")
