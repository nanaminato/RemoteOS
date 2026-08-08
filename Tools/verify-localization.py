"""Checks that shipped language packs have the same semantic keys as English."""
import json
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "Client" / "RemoteOS.Client"
language_dir = root / "Localization"
english = json.loads((language_dir / "en-US.json").read_text(encoding="utf-8"))["Strings"]
expected = set(english)
valid_key = re.compile(r"[a-z][a-z0-9]*(?:[._][a-z0-9]+)*$")
errors = [f"non-semantic English key: {key}" for key in expected if not valid_key.fullmatch(key)]

for path in language_dir.glob("*.json"):
    strings = json.loads(path.read_text(encoding="utf-8"))["Strings"]
    actual = set(strings)
    missing, extra = expected - actual, actual - expected
    if missing: errors.append(f"{path.name}: missing {', '.join(sorted(missing))}")
    if extra: errors.append(f"{path.name}: extra {', '.join(sorted(extra))}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

print(f"Localization verified: {len(expected)} keys in every language pack.")
