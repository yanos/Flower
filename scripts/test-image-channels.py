#!/usr/bin/env python3
"""Run the same release policy vectors as the desktop updater tests."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("channels", root / "scripts/image-channels.py")
channels = importlib.util.module_from_spec(spec)
spec.loader.exec_module(channels)
vectors = json.loads((root / "Tests/ReleasePolicyVectors.json").read_text())
for case in vectors["versions"]:
    version = channels.parse(case["tag"])
    assert (version is not None) == case["valid"], case
    eligible = channels.channel(version[1]) if version else None
    assert (eligible == "stable") == case["stable"], case
    assert (eligible in ("stable", "beta")) == case["beta"], case
for case in vectors["comparisons"]:
    left, right = channels.parse(case["left"]), channels.parse(case["right"])
    result = None if left is None or right is None else (channels.key(left) > channels.key(right)) - (channels.key(left) < channels.key(right))
    assert result == case["result"], case
# An older stable patch cannot move either image channel behind a newer release.
result = subprocess.run([sys.executable, str(root / "scripts/image-channels.py"), "v1.2.4"],
    input="v1.3.0\nv1.4.0-beta.1\n", text=True, capture_output=True, check=True)
assert result.stdout == "latest=false\nbeta=false\n", result.stdout
print("Release policy conformance passed")
