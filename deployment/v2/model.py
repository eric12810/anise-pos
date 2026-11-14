"""Environment and artifact validation shared by every V2 operation."""
from dataclasses import dataclass
from hashlib import sha256
import json
from pathlib import Path, PurePosixPath
import re

SHA = re.compile(r"[0-9a-f]{64}\Z")
COMMIT = re.compile(r"[0-9a-f]{40}\Z")
SAFE = re.compile(r"[A-Za-z0-9_./-]+\Z")
BLOCKED = {"appsettings.json", "web.config", "app_offline.htm"}
BLOCKED_PARTS = {"data", "app_data", "logs", "keys", "dataprotection-keys", "data-protection-keys", "database", ".git"}
BLOCKED_SUFFIXES = {".pfx", ".pem", ".key", ".bak", ".sql", ".pubxml"}


@dataclass(frozen=True)
class Target:
    name: str
    host: str
    user: str
    root: str
    url: str
    database: str

    def validate(self):
        expected = {
            "staging": ("stageposftp", "/stageposftp/wwwroot", "https://pos-staging.loveanise.co.uk", "anisedb"),
            "production": ("liveposftp", "/wwwroot", "https://pos.loveanise.co.uk", "anisedatabase"),
        }
        if self.name not in expected or (self.user, self.root, self.url, self.database) != expected[self.name]:
            raise ValueError("Target identity differs from approved environment mapping")
        if not re.fullmatch(r"[A-Za-z0-9.-]+", self.host) or not self.host:
            raise ValueError("Invalid FTPS host")


def safe_name(name):
    path = PurePosixPath(name)
    if (not name or not SAFE.fullmatch(name) or path.is_absolute() or ".." in path.parts
            or name.startswith(".") or any(part.lower() in BLOCKED_PARTS for part in path.parts)
            or path.name.lower() in BLOCKED or path.name.lower().startswith(("appsettings", ".env", "secrets", "credentials"))
            or path.suffix.lower() in BLOCKED_SUFFIXES):
        raise ValueError("Unsafe or protected package path")
    return name


def package(root):
    root = Path(root)
    manifest = root / "release-manifest.sha256"
    if not manifest.is_file():
        raise ValueError("Release checksum manifest missing")
    files = {}
    for line in manifest.read_text().splitlines():
        if "  " not in line:
            raise ValueError("Malformed checksum manifest")
        digest, name = line.split("  ", 1)
        safe_name(name)
        if not SHA.fullmatch(digest) or name in files:
            raise ValueError("Invalid or duplicate checksum entry")
        path = root / name
        if not path.is_file() or path.is_symlink() or root.resolve() not in path.resolve().parents:
            raise ValueError("Package file missing or escaped package root")
        if sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError("Package checksum mismatch")
        files[name] = digest
    entries = list(root.rglob("*"))
    if any(p.is_symlink() for p in entries):
        raise ValueError("Package contains a symbolic link")
    actual = {str(p.relative_to(root)).replace("\\", "/") for p in entries if p.is_file()}
    if actual != set(files) | {"release-manifest.sha256"}:
        raise ValueError("Unlisted package files found")
    if "AnisePOS.Api.dll" not in files or "wwwroot/pos.html" not in files:
        raise ValueError("Required POS files missing")
    return files, sha256(manifest.read_bytes()).hexdigest()


def state_bytes(target, release, artifact, phase, files, previous=None, history=None):
    if phase not in {"uploaded", "installed", "healthy", "failed"} or not COMMIT.fullmatch(release) or not SHA.fullmatch(artifact):
        raise ValueError("Invalid release state")
    history = history or [phase]
    if history[-1] != phase or any(item not in {"uploaded", "installed", "healthy", "failed"} for item in history):
        raise ValueError("Invalid release transition history")
    return (json.dumps({"version": 2, "environment": target.name, "application": target.url,
                        "release": release, "artifact": artifact, "phase": phase,
                        "files": files, "previous_healthy": previous, "history": history}, sort_keys=True, separators=(",", ":")) + "\n").encode()


def parse_state(raw, target):
    obj = json.loads(raw)
    if (obj.get("version") != 2 or obj.get("environment") != target.name
            or obj.get("application") != target.url or obj.get("phase") not in {"uploaded", "installed", "healthy", "failed"}
            or not COMMIT.fullmatch(obj.get("release", "")) or not SHA.fullmatch(obj.get("artifact", ""))
            or not isinstance(obj.get("files"), dict)):
        raise ValueError("Invalid or foreign release state")
    if (not isinstance(obj.get("history"), list) or not obj["history"]
            or obj["history"][-1] != obj["phase"]
            or any(item not in {"uploaded", "installed", "healthy", "failed"} for item in obj["history"])):
        raise ValueError("Invalid release transition history")
    for name, digest in obj["files"].items():
        safe_name(name)
        if not SHA.fullmatch(digest):
            raise ValueError("Invalid release file digest")
    return obj
