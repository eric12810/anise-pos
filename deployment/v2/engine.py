"""One release transaction for staging, production and approved application rollback."""
from hashlib import sha256
import io
import json
from pathlib import Path, PurePosixPath
import secrets
import time
from urllib.request import urlopen
import zipfile

from .model import package, parse_state, state_bytes

CURRENT = ".anise-v2-current.json"
HEALTHY = ".anise-v2-healthy.json"
OFFLINE = "app_offline.htm"


def record_name(release):
    return ".anise-v2-" + release + ".json"


def read_state(ftp, target, name):
    return parse_state(ftp.get(name), target)


def verified_put(ftp, name, content):
    ftp.put(name, content)
    if sha256(ftp.get(name)).digest() != sha256(content).digest():
        raise RuntimeError("FTPS download verification failed: " + name)


def publish_state(ftp, target, name, content):
    """Publish one state document with Winserve's verified same-directory rename."""
    ftp.cwd(target.root)
    pending, previous = name + ".pending", name + ".previous"
    names = set(ftp.list())
    if pending in names or previous in names:
        raise RuntimeError("Interrupted release-state publication requires review")
    verified_put(ftp, pending, content)
    had_previous = name in names
    if had_previous:
        ftp.rename(name, previous)
    try:
        ftp.rename(pending, name)
    except Exception:
        if had_previous:
            ftp.rename(previous, name)
        raise
    if ftp.get(name) != content:
        raise RuntimeError("Published release state differs from verified candidate")
    if had_previous:
        ftp.delete(previous)


def disposable_ftps_probe(ftp, target):
    """Exercise V2 upload, download, rename and delete on one disposable file."""
    if target.name != "staging":
        raise ValueError("Disposable FTPS probe is staging-only")
    ftp.cwd(target.root)
    token = secrets.token_hex(8)
    source, renamed = ".anise-v2-probe-" + token, ".anise-v2-probe-" + token + ".renamed"
    names = set(ftp.list())
    if OFFLINE in names or source in names or renamed in names:
        raise RuntimeError("Staging site is not ready for disposable FTPS probe")
    content = secrets.token_bytes(32)
    verified_put(ftp, source, content)
    ftp.rename(source, renamed)
    if ftp.get(renamed) != content:
        raise RuntimeError("Disposable FTPS rename verification failed")
    ftp.delete(renamed)
    if source in ftp.list() or renamed in ftp.list():
        raise RuntimeError("Disposable FTPS probe cleanup failed")
    return "staging FTPS upload, download, rename and delete verified"


def directory_for(ftp, target, name):
    ftp.cwd(target.root)
    for part in PurePosixPath(name).parent.parts:
        if part == ".":
            continue
        if part not in ftp.list():
            ftp.mkdir(part)
        ftp.cwd(part)


def install_files(ftp, target, root, files, verified_existing=None):
    for name, digest in sorted(files.items()):
        # The caller has already downloaded and hashed every managed live file.
        if verified_existing is not None and verified_existing.get(name) == digest:
            continue
        directory_for(ftp, target, name)
        data = (Path(root) / name).read_bytes()
        if sha256(data).hexdigest() != digest:
            raise ValueError("Local artifact changed during deployment")
        verified_put(ftp, PurePosixPath(name).name, data)
    ftp.cwd(target.root)


def health(url, *, attempts=18, interval=10, opener=urlopen, sleeper=time.sleep):
    last = "no response"
    for number in range(attempts):
        try:
            with opener(url + "/health", timeout=20) as response:
                body = json.load(response)
                if response.status == 200 and body.get("status") == "ok" and body.get("database") == "ok":
                    return
                last = "health response did not confirm application and database"
        except Exception as error:
            last = type(error).__name__
        if number + 1 < attempts:
            sleeper(interval)
    raise RuntimeError("POS did not become healthy after startup grace/retries: " + last)


def inspect(ftp, target):
    ftp.cwd(target.root)
    names = set(ftp.list())
    if OFFLINE in names:
        raise RuntimeError("Existing IIS maintenance marker requires recovery review")
    if any(name.startswith(".anise-v2-") and name.endswith((".pending", ".previous")) for name in names):
        raise RuntimeError("Interrupted release-state publication requires recovery review")
    if CURRENT not in names or HEALTHY not in names:
        raise RuntimeError("V2 healthy baseline is absent; seed only after a reviewed activation")
    current = read_state(ftp, target, CURRENT)
    healthy_state = read_state(ftp, target, HEALTHY)
    if current != healthy_state or current["phase"] != "healthy":
        raise RuntimeError("Current application is not the verified healthy baseline")
    if record_name(current["release"]) not in names or read_state(ftp, target, record_name(current["release"])) != current:
        raise RuntimeError("Healthy release history is missing or inconsistent")
    return current


def seed(ftp, target, root, release, *, health_check=health):
    """One-time, explicitly approved baseline from a byte-identical live artifact."""
    files, artifact = package(root)
    ftp.cwd(target.root)
    names = set(ftp.list())
    if (OFFLINE in names or CURRENT in names or HEALTHY in names
            or any(name.startswith(".anise-v2-") or name.endswith((".pending", ".previous"))
                   for name in names)):
        raise RuntimeError("Baseline seed requires a clean, unseeded healthy site")
    if "web.config" not in names:
        raise RuntimeError("Hosted IIS web.config is absent")
    for name, digest in files.items():
        try:
            actual = sha256(ftp.get(name)).hexdigest()
        except (KeyError, RuntimeError) as error:
            raise RuntimeError("Live baseline file missing: " + name) from error
        if actual != digest:
            raise RuntimeError("Live application differs from reviewed baseline: " + name)
    health_check(target.url)
    healthy = state_bytes(target, release, artifact, "healthy", files)
    publish_state(ftp, target, record_name(release), healthy)
    publish_state(ftp, target, HEALTHY, healthy)
    publish_state(ftp, target, CURRENT, healthy)
    return "V2 baseline verified healthy"


def backup_current(ftp, target, destination):
    """Read-only, verified managed-file backup; caller retains it before any upload."""
    current = inspect(ftp, target)
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        archive.writestr("release-state.json", state_bytes(target, current["release"], current["artifact"],
                                                           "healthy", current["files"], current["previous_healthy"], current["history"]))
        for name, digest in sorted(current["files"].items()):
            content = ftp.get(name)
            if sha256(content).hexdigest() != digest:
                raise RuntimeError("Live managed file differs from healthy baseline: " + name)
            archive.writestr("files/" + name, content)
    with zipfile.ZipFile(destination) as archive:
        if parse_state(archive.read("release-state.json"), target) != current:
            raise RuntimeError("Application archive state verification failed")
        for name, digest in current["files"].items():
            if sha256(archive.read("files/" + name)).hexdigest() != digest:
                raise RuntimeError("Application archive checksum verification failed")
    return current


def deploy(ftp, target, root, release, *, health_check=health, sleeper=time.sleep):
    files, artifact = package(root)
    current = inspect(ftp, target)
    if current["release"] == release and current["artifact"] == artifact and current["files"] == files:
        health_check(target.url)
        return "already healthy"
    if record_name(release) in ftp.list():
        raise RuntimeError("Release ID already has history; refusing replay")
    # No implicit removal: a removed DLL may still be loaded by IIS.
    if set(current["files"]) - set(files):
        raise RuntimeError("Artifact removes managed files; explicit compatibility review required")
    for name, digest in current["files"].items():
        if sha256(ftp.get(name)).hexdigest() != digest:
            raise RuntimeError("Live managed file changed since healthy baseline: " + name)
    marker = b"<html><body>POS deployment in progress</body></html>\n"
    transitions = []
    verified_put(ftp, OFFLINE, marker)
    try:
        sleeper(15)
        install_files(ftp, target, root, files, current["files"])
        uploaded = state_bytes(target, release, artifact, "uploaded", files, current["release"])
        transitions.append("uploaded")
        publish_state(ftp, target, record_name(release), uploaded)
        installed = state_bytes(target, release, artifact, "installed", files, current["release"], transitions + ["installed"])
        transitions.append("installed")
        publish_state(ftp, target, record_name(release), installed)
        ftp.delete(OFFLINE)
        health_check(target.url)
        healthy = state_bytes(target, release, artifact, "healthy", files, current["release"], transitions + ["healthy"])
        publish_state(ftp, target, record_name(release), healthy)
        publish_state(ftp, target, HEALTHY, healthy)
        publish_state(ftp, target, CURRENT, healthy)
        return "verified healthy"
    except Exception:
        # Best effort: if FTPS itself is broken, the last written phase and marker
        # still tell operators the release must be inspected before recovery.
        try:
            ftp.cwd(target.root)
            if OFFLINE not in ftp.list():
                verified_put(ftp, OFFLINE, marker)
            failed = state_bytes(target, release, artifact, "failed", files, current["release"], transitions + ["failed"])
            publish_state(ftp, target, record_name(release), failed)
        except Exception:
            pass
        raise


def rollback(ftp, target, archive, expected_failed_release, *, health_check=health, sleeper=time.sleep):
    """Restore only an exact prior healthy application archive; never touch SQL/config."""
    ftp.cwd(target.root)
    names = set(ftp.list())
    if OFFLINE not in names or record_name(expected_failed_release) not in names:
        raise RuntimeError("Expected failed release and maintenance marker are absent")
    failed = read_state(ftp, target, record_name(expected_failed_release))
    healthy_state = read_state(ftp, target, HEALTHY)
    if failed["phase"] != "failed" or failed["previous_healthy"] != healthy_state["release"]:
        raise RuntimeError("Failed release does not point to the last verified healthy release")
    with zipfile.ZipFile(archive) as source:
        archive_state = parse_state(source.read("release-state.json"), target)
        if archive_state != healthy_state:
            raise RuntimeError("Recovery archive differs from last verified healthy release")
        payloads = {}
        if set(source.namelist()) != {"release-state.json"} | {"files/" + x for x in healthy_state["files"]}:
            raise RuntimeError("Recovery archive contains missing or unexpected files")
        for name, digest in healthy_state["files"].items():
            data = source.read("files/" + name)
            if sha256(data).hexdigest() != digest:
                raise RuntimeError("Recovery archive checksum mismatch")
            payloads[name] = data
    added = sorted(set(failed["files"]) - set(healthy_state["files"]))
    for name in added:
        if sha256(ftp.get(name)).hexdigest() != failed["files"][name]:
            raise RuntimeError("Failed-release-only file changed; rollback refused: " + name)
    sleeper(15)
    for name, data in sorted(payloads.items()):
        directory_for(ftp, target, name)
        verified_put(ftp, PurePosixPath(name).name, data)
    for name in added:
        directory_for(ftp, target, name)
        ftp.delete(PurePosixPath(name).name)
    ftp.cwd(target.root)
    ftp.delete(OFFLINE)
    try:
        health_check(target.url)
    except Exception:
        verified_put(ftp, OFFLINE, b"<html><body>POS recovery review required</body></html>\n")
        raise
    # Keep the failed release record; current points back to the historical healthy one.
    publish_state(ftp, target, CURRENT, state_bytes(target, healthy_state["release"], healthy_state["artifact"],
                                              "healthy", healthy_state["files"], healthy_state["previous_healthy"], healthy_state["history"]))
    return "verified healthy after rollback"
