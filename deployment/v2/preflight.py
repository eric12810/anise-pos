"""Fail-closed validation of protected, independently collected release evidence.

This does not manufacture hosting or SQL evidence. A reviewer supplies recent
metadata-only SQL results without giving GitHub an unrestricted SQL credential.
"""
from datetime import datetime, timezone, timedelta
import json
import re

from .model import SHA


def validate_evidence(raw, target, artifact, *, now=None):
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
            review = evidence["manual_sql_review"]
            reviewed = datetime.fromisoformat(review["checked_at"].replace("Z", "+00:00"))
            if (reviewed.tzinfo is None or not now - timedelta(hours=24) <= reviewed <= now + timedelta(minutes=5)
                    or review["method"] != "manual_readonly_metadata"
                    or not re.fullmatch(r"[A-Za-z0-9_.-]{3,80}", review["reviewed_by"])
                    or not re.fullmatch(r"[A-Za-z0-9_./:-]{12,200}", review["record_reference"])
                    or review["server"] != evidence["sql_server"]
                    or review["database"] != target.database
                    or review["artifact_sha256"] != artifact
                    or review["mapped_columns_checked"] is not True
                    or review["missing_mapped_columns"] != []
                    or review["required_index_present"] is not True
                    or review["migration_checksums_checked"] is not True):
                raise ValueError("Fresh reviewed production SQL evidence is required")
        if evidence["artifact_sha256"] != artifact or not SHA.fullmatch(artifact):
            raise ValueError("Evidence does not cover this exact artifact")
        if evidence["effective_iis_config_checked"] is not True:
            raise ValueError("Active IIS configuration verification is required")
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
        if evidence.get("pending_sql_migrations") != []:
            raise ValueError("V2 does not execute SQL migrations; pending changes require a reviewed integration")
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("Incomplete protected preflight evidence") from error
    return True
