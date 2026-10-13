#!/usr/bin/env python3
"""Next build number for a release (iOS + Android share it): increments "build" in Tools/release/version.json
and prints "<version> <build>". The marketing version ("1.0") is edited by hand for a new version.
Android versionCode = 1000000 + build (above the old date-based codes, so updates install over old builds)."""
import json
import os

path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "version.json")
with open(path) as f:
    v = json.load(f)
v["build"] = int(v["build"]) + 1
with open(path, "w") as f:
    json.dump(v, f, indent=2)
    f.write("\n")
print(v["version"], v["build"])
