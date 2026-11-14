"""Fail-closed validation of protected, independently collected release evidence.

This does not manufacture hosting or SQL evidence. The workflow must obtain it
from authorised read-only host/SQL probes and a tested backup provider.
"""
from datetime import datetime, timezone, timedelta
import json
import re

from .model import SHA


def validate_evidence(raw, target, artifact, *, db_probe=None, now=None):
    target.validate()
    evidence = json.loads(raw)
    now = now or datetime.now(timezone.utc)
    try:
        checked = datetime.fromisoformat(evidence["checked_at"].replace("Z", "+00:00"))
        if checked.tzinfo is None or not now - timedelta(hours=24) <= checked <= now + timedelta(minutes=5):
            raise ValueError("Preflight evidence is stale or future dated")
        if evidence["environment"] != target.name or evidence["site"] != target.url:
            raise ValueError("Preflight site identity mismatch")
        if evidence["ftps_host"] != target.host or evidence["ftps_root"] != target.root or evidence["ftps_user"] != target.user:
            raise ValueError("Preflight FTPS identity mismatch")
        if evidence["sql_database"] != target.database or not evidence["sql_server"]:
            raise ValueError("Read-only SQL identity mismatch")
        if target.name == "production":
            if db_probe is None:
                raise ValueError("Live read-only SQL probe required")
            probe = json.loads(db_probe)
            if (probe.get("database") != target.database or probe.get("server") != evidence["sql_server"]
                    or probe.get("connected") is not True or probe.get("schema_compatible") is not True
                    or probe.get("missing_columns") != [] or probe.get("missing_required_index") is not False):
                raise ValueError("Live SQL identity or mapped schema does not match protected evidence")
        if evidence["artifact_sha256"] != artifact or not SHA.fullmatch(artifact):
            raise ValueError("Evidence does not cover this exact artifact")
        if evidence["effective_iis_config_checked"] is not True or evidence["sql_readonly_probe_passed"] is not True:
            raise ValueError("Active IIS configuration and read-only SQL probe are required")
        if evidence["schema_compatible"] is not True or evidence["runtime_compatible"] is not True:
            raise ValueError("Schema or Winserve runtime compatibility unverified")
        if evidence["startup_logging_verified"] is not True:
            raise ValueError("Secure startup logging in active IIS site unverified")
        backups = evidence["backups"]
        for kind in ("application", "database"):
            backup = backups[kind]
            if (not re.fullmatch(r"[A-Za-z0-9_./:-]{12,200}", backup["reference"])
                    or backup["restorable"] is not True or backup["retained"] is not True):
                raise ValueError("Verified, retained, restorable " + kind + " backup required")
        if backups["application"]["reference"] == backups["database"]["reference"]:
            raise ValueError("Application and database backup references must differ")
        if evidence.get("pending_sql_migrations") not in ([], None):
            raise ValueError("V2 does not execute SQL migrations; pending changes require a reviewed integration")
    except (KeyError, TypeError) as error:
        raise ValueError("Incomplete protected preflight evidence") from error
    return True
