"""Verify every payload hash in portable releases without executing account queries."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import zipfile

dist = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / "dist"
archives = sorted(dist.glob("*-Portable-x64.zip"))
if not archives:
    raise SystemExit("No portable archives found")
for archive in archives:
    with zipfile.ZipFile(archive) as z:
        names = z.namelist()
        assert len(names) == len(set(names)), "Duplicate ZIP entries"
        for name in names:
            path = PurePosixPath(name)
            assert not path.is_absolute() and ".." not in path.parts and ":" not in name, name
            assert path.name.lower() not in {"auth.json", "settings.json", "notifications-v1.json", "last-error.txt"}, name
        manifest = json.loads(z.read("manifest.json"))
        expected = {entry["path"] for entry in manifest["files"]}
        assert set(names) == expected | {"manifest.json"}, "Manifest does not cover the complete payload"
        for entry in manifest["files"]:
            data = z.read(entry["path"])  # zipfile checks the CRC as it reads.
            assert len(data) == entry["size"], entry["path"]
            assert hashlib.sha256(data).hexdigest() == entry["sha256"], entry["path"]
        for required in ("CodexMeter.exe", "CodexMeter.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "LICENSE", "licenses/dotnet-runtime-license.txt", "licenses/dotnet-third-party-notices.txt", "licenses/windowsdesktop-license.txt"):
            assert required in expected, required
        config = json.loads(z.read("CodexMeter.runtimeconfig.json"))["runtimeOptions"]
        frameworks = config.get("includedFrameworks", [])
        assert len(frameworks) >= 2 and all(x["version"] == manifest["runtime"] for x in frameworks), "Runtime is not self-contained/pinned"
        assert manifest["edition"] in archive.name and manifest["version"] in archive.name
        print(f"{archive.name}: {len(expected)} files verified, bundled .NET {manifest['runtime']}")
