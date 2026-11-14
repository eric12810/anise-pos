"""Read-only checks around a guarded, exact-artifact staging baseline seed."""
import argparse
from hashlib import sha256
import json
from pathlib import Path
from urllib.request import urlopen

from .cli import target_from_env
from .engine import CURRENT, HEALTHY, OFFLINE, health, record_name
from .model import package, parse_state, safe_name
from .transport import CurlFTPS
import os


def original_artifact(root):
    root = Path(root)
    rows = (root / "release-manifest.sha256").read_text().splitlines()
    files = {}
    for row in rows:
        digest, name = row.split("  ", 1)
        if name != "web.config":
            safe_name(name)
        if name in files or len(digest) != 64 or sha256((root / name).read_bytes()).hexdigest() != digest:
            raise ValueError("Original artifact checksum or file list invalid")
        files[name] = digest
    actual = {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file()}
    if len(files) != 126 or "web.config" not in files or actual != set(files) | {"release-manifest.sha256"}:
        raise ValueError("Original artifact is not the expected 126-file release")
    print("Original artifact: 126/126 checksums match")


def connection():
    target = target_from_env("staging")
    target.validate()
    return target, CurlFTPS(target, os.environ.get("V2_STAGING_FTPS_PASSWORD", ""))


def live(root):
    files, artifact = package(root)
    if len(files) != 125:
        raise ValueError("Expected exactly 125 managed files")
    target, ftp = connection()
    ftp.cwd(target.root)
    names = set(ftp.list())
    if (OFFLINE in names or "web.config" not in names or any(
            name.startswith(".anise-v2-") or name.endswith((".pending", ".previous")) for name in names)):
        raise RuntimeError("Interrupted marker, existing V2 state, or missing hosted web.config")
    missing = []
    mismatched = []
    for name, expected in sorted(files.items()):
        try:
            actual = sha256(ftp.get(name)).hexdigest()
        except RuntimeError:
            missing.append(name)
            continue
        if actual != expected:
            mismatched.append((name, expected, actual))
    print(f"Remote files: {len(files)-len(missing)-len(mismatched)} matched, {len(missing)} missing, {len(mismatched)} mismatched")
    for name in missing:
        print("Missing:", name)
    for name, expected, actual in mismatched:
        print("Mismatch:", name, "expected", expected, "actual", actual)
    if missing or mismatched:
        raise RuntimeError("Remote staging files differ from exact artifact")
    health(target.url, attempts=1)
    with urlopen(target.url + "/staff-login.html", timeout=20) as response:
        if response.status != 200:
            raise RuntimeError("Staging login page unhealthy")
    with urlopen(target.url + "/api/menu", timeout=20) as response:
        if response.status != 200 or not json.load(response):
            raise RuntimeError("Staging menu API unhealthy")
    print("Staging health, login page and menu API passed; manifest SHA-256", artifact)


def state(root, release):
    _, artifact = package(root)
    target, ftp = connection()
    ftp.cwd(target.root)
    records = [parse_state(ftp.get(name), target) for name in (CURRENT, HEALTHY, record_name(release))]
    if not (records[0] == records[1] == records[2] and records[0]["release"] == release
            and records[0]["artifact"] == artifact and records[0]["phase"] == "healthy"):
        raise RuntimeError("Seeded state records disagree")
    health(target.url, attempts=1)
    print("Three V2 state records agree; final staging health passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("original", "live", "state"))
    parser.add_argument("root")
    parser.add_argument("--release", default="")
    args = parser.parse_args()
    if args.command == "original":
        original_artifact(args.root)
    elif args.command == "live":
        live(args.root)
    else:
        state(args.root, args.release)
