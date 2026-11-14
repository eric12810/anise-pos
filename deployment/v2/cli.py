"""V2 command line entry point; no deployment workflow invokes it until activation."""
import argparse
import os
from pathlib import Path
import sys

from .engine import backup_current, deploy, rollback, seed, disposable_ftps_probe
from .model import Target, package
from .preflight import validate_evidence
from .transport import CurlFTPS


def target_from_env(name):
    prefix = "V2_" + name.upper() + "_"
    return Target(name, os.environ.get(prefix + "FTPS_HOST", ""), os.environ.get(prefix + "FTPS_USER", ""),
                  os.environ.get(prefix + "FTPS_ROOT", ""), os.environ.get(prefix + "SITE_URL", ""),
                  os.environ.get(prefix + "DATABASE", ""))


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("package", "preflight", "backup", "deploy", "rollback", "seed", "ftps-probe"))
    parser.add_argument("--environment", choices=("staging", "production"), required=True)
    parser.add_argument("--artifact", default="artifacts/staging")
    parser.add_argument("--release", default="")
    parser.add_argument("--archive", default="")
    parser.add_argument("--evidence", default="")
    parser.add_argument("--db-probe", default="")
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
        if not args.evidence or not args.db_probe:
            raise ValueError("Protected production evidence and live read-only SQL probe are required")
        validate_evidence(Path(args.evidence).read_text(), target, digest,
                          db_probe=Path(args.db_probe).read_text())
    if args.command == "preflight":
        print("Protected preflight evidence validated for exact artifact")
        return
    staging_seed = args.command == "seed" and args.environment == "staging"
    if not staging_seed and os.environ.get("V2_ACTIVATED") != "yes":
        raise ValueError("V2 is dormant until explicit workflow activation")
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


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # No server replies, passwords or raw evidence are printed.
        print("V2 stopped safely:", type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
