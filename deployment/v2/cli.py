"""V2 command line entry point; no deployment workflow invokes it until activation."""
import argparse
import json
import os
import stat
from pathlib import Path
import sys

from .engine import backup_current, backup_legacy, deploy, deploy_initial, rollback, seed, disposable_ftps_probe
from .model import Target, package
from .preflight import validate_evidence
from .transport import CurlFTPS


def target_from_env(name):
    prefix = "V2_" + name.upper() + "_"
    return Target(name, os.environ.get(prefix + "FTPS_HOST", ""), os.environ.get(prefix + "FTPS_USER", ""),
                  os.environ.get(prefix + "FTPS_ROOT", ""), os.environ.get(prefix + "SITE_URL", ""),
                  os.environ.get(prefix + "DATABASE", ""))


def read_private(path):
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    with os.fdopen(descriptor, "rb") as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or stat.S_IMODE(info.st_mode) & 0o077:
            raise ValueError("Protected evidence file must be private (mode 0600)")
        raw = stream.read(1_000_001)
        if len(raw) > 1_000_000:
            raise ValueError("Protected evidence file is oversized")
        return raw


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("package", "preflight", "backup", "deploy", "initial-backup", "initial-deploy", "rollback", "seed", "ftps-probe"))
    parser.add_argument("--environment", choices=("staging", "production"), required=True)
    parser.add_argument("--artifact", default="artifacts/staging")
    parser.add_argument("--release", default="")
    parser.add_argument("--archive", default="")
    parser.add_argument("--evidence", default="")
    parser.add_argument("--sql-snapshot", default="")
    parser.add_argument("--expected-schema", default="")
    parser.add_argument("--db-probe", default="")
    parser.add_argument("--sql-mode", choices=("manual", "automated"), default="")
    args = parser.parse_args(argv)
    target = target_from_env(args.environment)
    target.validate()
    if args.command == "rollback":
        if not os.environ.get("V2_ACTIVATED") == "yes" or args.environment != "production" or os.environ.get("V2_ROLLBACK_APPROVAL") != "ROLLBACK POS PRODUCTION":
            raise ValueError("Protected production rollback approval required")
        if not args.archive or len(args.release) != 40:
            raise ValueError("Exact failed release and recovery archive required")
        ftp = CurlFTPS(target, os.environ.get("V2_PRODUCTION_FTPS_PASSWORD", ""))
        print(rollback(ftp, target, args.archive, args.release))
        return
    if args.command == "ftps-probe":
        if args.environment != "staging" or os.environ.get("V2_FTPS_PROBE_APPROVAL") != "PROBE STAGING FTPS":
            raise ValueError("Explicit disposable staging FTPS probe approval required")
        ftp = CurlFTPS(target, os.environ.get("V2_STAGING_FTPS_PASSWORD", ""))
        print(disposable_ftps_probe(ftp, target))
        return
    files, digest = package(args.artifact)
    if args.command == "package":
        print("Validated package:", len(files), "files; manifest SHA-256", digest)
        return
    if args.environment == "production":
        if not args.evidence:
            raise ValueError("Protected production preflight evidence is required")
        evidence_raw = read_private(args.evidence).decode("utf-8")
        mode = json.loads(evidence_raw).get("sql_mode")
        if args.sql_mode != mode:
            raise ValueError("Explicit SQL mode must match protected evidence")
        snapshot_path = args.sql_snapshot or "sql-snapshot.json"
        schema_path = args.expected_schema or "expected-schema.json"
        probe_path = args.db_probe or "db-probe.json"
        validate_evidence(evidence_raw, target, digest, release=args.release,
                          sql_snapshot=read_private(snapshot_path) if mode == "manual" else None,
                          expected_schema=read_private(schema_path) if mode == "manual" else None,
                          db_probe=read_private(probe_path).decode("utf-8") if mode == "automated" else None)
    if args.command == "preflight":
        print("Protected preflight evidence validated for exact artifact")
        return
    staging_seed = args.command == "seed" and args.environment == "staging"
    if not staging_seed and os.environ.get("V2_ACTIVATED") != "yes":
        raise ValueError("V2 is dormant until explicit workflow activation")
    if args.command in ("initial-backup", "initial-deploy"):
        if (args.environment != "production" or os.environ.get("V2_INITIAL_RELEASE_APPROVAL") != "APPROVE FIRST POS V2 PRODUCTION RELEASE"
                or not args.archive or len(args.release) != 40):
            raise ValueError("Exact first production release approval and archive required")
    if args.command == "seed":
        if os.environ.get("V2_SEED_APPROVAL") != "SEED POS V2 BASELINE" or len(args.release) != 40:
            raise ValueError("Reviewed baseline approval and exact commit SHA required")
    prefix = "V2_" + args.environment.upper() + "_"
    ftp = CurlFTPS(target, os.environ.get(prefix + "FTPS_PASSWORD", ""))
    if args.command == "backup":
        if not args.archive:
            raise ValueError("Backup archive path required")
        state = backup_current(ftp, target, args.archive)
        print("Verified application archive for healthy release", state["release"])
    elif args.command == "deploy":
        if len(args.release) != 40:
            raise ValueError("Exact commit SHA required")
        print(deploy(ftp, target, args.artifact, args.release))
    elif args.command == "seed":
        print(seed(ftp, target, args.artifact, args.release))
    elif args.command == "initial-backup":
        files = backup_legacy(ftp, target, args.archive)
        print("Archived", len(files), "legacy managed files; prior application health unverified")
    elif args.command == "initial-deploy":
        print(deploy_initial(ftp, target, args.artifact, args.release, args.archive))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # No server replies, passwords or raw evidence are printed.
        if len(sys.argv) > 1 and sys.argv[1] == "preflight" and isinstance(error, ValueError):
            # Preflight validators return fixed, non-secret failure descriptions.
            print("V2 preflight stopped:", str(error), file=sys.stderr)
        else:
            print("V2 stopped safely:", type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
