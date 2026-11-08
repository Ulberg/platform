#!/usr/bin/env python3
"""CI check: every file in .prompts/ follows the format in AGENTS.md §1."""

import re
import sys
from pathlib import Path

NAME = re.compile(r"^\d{6}Z_[a-z0-9-]+_(user|assistant)\.txt$")
HEADER = re.compile(
    r"^author: [a-z0-9-]+\nrole: (user|assistant)\ntimestamp: \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\nsession: \S+\n\n"
)
ALLOWED = {".gitkeep"}

errors = []
for path in sorted(Path(".prompts").rglob("*")):
    if path.is_dir() or path.name in ALLOWED or ".state" in path.parts:
        continue
    rel = path.as_posix()
    if not re.fullmatch(r"\.prompts/\d{4}-\d\d-\d\d", path.parent.as_posix()) or not NAME.match(path.name):
        errors.append(f"{rel}: wrong path, expected .prompts/<YYYY-MM-DD>/<HHMMSS>Z_<author>_<role>.txt")
        continue
    text = path.read_text(encoding="utf-8")
    header = HEADER.match(text)
    if not header:
        errors.append(f"{rel}: missing or malformed header")
    elif header.group(1) != path.name.rsplit("_", 1)[1].removesuffix(".txt"):
        errors.append(f"{rel}: role in header does not match file name")
    elif path.parent.name != text.split("timestamp: ", 1)[1][:10]:
        errors.append(f"{rel}: timestamp date does not match folder")

print("\n".join(errors) or "prompt log ok")
sys.exit(1 if errors else 0)
