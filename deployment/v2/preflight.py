"""Fail-closed validation of protected, independently collected release evidence.

This does not manufacture hosting or SQL evidence. A reviewer supplies recent
metadata-only SQL results without giving GitHub an unrestricted SQL credential.
"""
from datetime import datetime, timezone, timedelta
import json
import re
from . import manual_sql

from .model import SHA


def validate_evidence(raw, target, artifact, *, now=None, sql_snapshot=None, expected_schema=None,
                      release=None, db_probe=None):
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
            raise ValueError("SQL identity mismatch")
        if target.name == "production":
            mode = evidence["sql_mode"]
            if mode == "manual":
                if sql_snapshot is None or expected_schema is None or release is None:
                    raise ValueError("Private SQL snapshot and exact release model required")
                manual_sql.validate(sql_snapshot, expected_schema, evidence["manual_sql_review"], artifact, release, now=now)
                if evidence["sql_server"] != evidence["manual_sql_review"]["observed_server"]:
                    raise ValueError("SQL server observation mismatch")
            elif mode == "automated":
                if db_probe is None:
                    raise ValueError("Protected read-only SQL probe required")
                probe = json.loads(db_probe)
                if (probe["server"] != evidence["sql_server"] or probe["database"] != target.database
                        or probe["connected"] is not True or probe["permission_restricted"] is not True
                        or probe["schema_compatible"] is not True or probe["missing_columns"] != []
                        or probe["missing_required_index"] is not False):
                    raise ValueError("Automated read-only SQL probe failed")
            else:
                raise ValueError("Explicit SQL preflight mode required")
        if evidence["artifact_sha256"] != artifact or not SHA.fullmatch(artifact):
            raise ValueError("Evidence does not cover this exact artifact")
        if evidence["effective_iis_config_checked"] is not True:
            raise ValueError("Active IIS configuration verification is required")
        if evidence["schema_compatible"] is not True or evidence["runtime_compatible"] is not True:
            raise ValueError("Schema or Winserve runtime compatibility unverified")
        logging = evidence["startup_logging"]
        if logging == "verified":
            if evidence["startup_logging_verified"] is not True:
                raise ValueError("Secure startup logging in active IIS site unverified")
        elif logging == "host_unavailable":
            if (evidence["startup_logging_verified"] is not False
                    or evidence["startup_logging_limitation_acknowledged"] is not True):
                raise ValueError("Hosted startup logging limitation must be explicitly acknowledged")
        else:
            raise ValueError("Startup logging status missing or invalid")
        backups = evidence["backups"]
        for kind in ("application", "database"):
            backup = backups[kind]
            if (not re.fullmatch(r"[A-Za-z0-9_./:-]{12,200}", backup["reference"])
                    or backup["restorable"] is not True or backup["retained"] is not True):
                raise ValueError("Verified, retained, restorable " + kind + " backup required")
        if backups["application"]["reference"] == backups["database"]["reference"]:
            raise ValueError("Application and database backup references must differ")
        if evidence.get("pending_sql_migrations") != []:
            raise ValueError("V2 does not execute SQL migrations; pending changes require a reviewed integration")
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("Incomplete protected preflight evidence") from error
    return True
