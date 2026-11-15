"""Validate a private, metadata-only SQL snapshot against the release EF model."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
import re
from pathlib import Path

SHA = re.compile(r"[0-9a-f]{64}\Z")
BASELINE_RELEASE = "bbfd19864093158a8d2123cc34bae6c97eb92049"
BASELINE_MANIFEST = "0041791ecf107370e054202ba3d4e9961b003c8908e985f21b06ff2784882c7b"
REQUIRED_COLUMNS = {
    "dbo.Orders.CancelledAtUtc": ("datetimeoffset", True),
    "dbo.Orders.CancellationReason": ("nvarchar", True),
    "dbo.Orders.ReopenedAtUtc": ("datetimeoffset", True),
    "dbo.Refunds.Id": ("uniqueidentifier", False),
    "dbo.Refunds.OrderId": ("uniqueidentifier", False),
    "dbo.WebsiteCheckouts.ReconciliationError": ("nvarchar", True),
    "dbo.WebsiteCheckouts.ReconciliationCheckedAtUtc": ("datetimeoffset", True),
}
EXPLICIT_LENGTHS = {
    "dbo.Orders.CancellationReason": 200,
    "dbo.WebsiteCheckouts.ReconciliationError": 300,
    "dbo.DeliverySettings.OrderSound": 20,
    "dbo.DeliverySettings.PrinterIpAddress": 15,
    "dbo.Promotions.ImageContentType": 20,
}
LEGACY_FINITE_LENGTHS = {"dbo.Products.VeganSection": 30}
REQUIRED_INDEXES = {
    "dbo.Refunds.IX_Refunds_OrderId_CreatedAtUtc": False,
    "dbo.Payments.UX_Payments_StripeCheckout_Reference": True,
    "dbo.StaffPushSubscriptions.UX_StaffPushSubscriptions_EndpointHash": True,
    "dbo.StaffPushDeliveries.UX_StaffPushDeliveries_Subscription_Order": True,
    "dbo.PromoRedemptions.IX_PromoRedemptions_Code_Phone": False,
    "dbo.WebsiteCheckouts.UX_WebsiteCheckouts_RequestId": True,
    "dbo.WebsiteCheckouts.UX_WebsiteCheckouts_StripeSessionId": True,
    "dbo.WebsiteConfirmations.UX_WebsiteConfirmations_OrderId_Kind": True,
    "dbo.Payments.UX_Payments_WebsiteCash_Reference": True,
}
REQUIRED_INDEX_KEYS = {
    "dbo.Refunds.IX_Refunds_OrderId_CreatedAtUtc": ["OrderId", "CreatedAtUtc"],
    "dbo.Payments.UX_Payments_StripeCheckout_Reference": ["Provider", "ProviderReference"],
    "dbo.StaffPushSubscriptions.UX_StaffPushSubscriptions_EndpointHash": ["EndpointHash"],
    "dbo.StaffPushDeliveries.UX_StaffPushDeliveries_Subscription_Order": ["SubscriptionId", "OrderId"],
    "dbo.PromoRedemptions.IX_PromoRedemptions_Code_Phone": ["Code", "Phone"],
    "dbo.WebsiteCheckouts.UX_WebsiteCheckouts_RequestId": ["RequestId"],
    "dbo.WebsiteCheckouts.UX_WebsiteCheckouts_StripeSessionId": ["StripeSessionId"],
    "dbo.WebsiteConfirmations.UX_WebsiteConfirmations_OrderId_Kind": ["OrderId", "Kind"],
    "dbo.Payments.UX_Payments_WebsiteCash_Reference": ["Provider", "ProviderReference"],
}
REQUIRED_DEFAULTS = {
    "dbo.DeliverySettings.Mode": "radius",
    "dbo.DeliverySettings.SectorsJson": "[]",
    "dbo.DeliverySettings.RadiusBandsJson": "[]",
    "dbo.WebsiteConfirmations.Kind": "received",
    "dbo.Products.IsVegan": "0",
    "dbo.Orders.CustomFee": "0",
}
REQUIRED_CHECKS = {
    "DeliverySettings": (("id=1",), ("radiusmiles>=0", "radiusmiles<=100"),
                         ("fee>=0", "fee<=1000")),
    "ModifierGroups": (("minimumselections>=0", "maximumselections>=minimumselections"),),
    "OrderLineModifiers": (("quantity>0",),),
    "OrderLines": (("quantity>0",),),
}


def _time(value, now):
    stamp = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if stamp.tzinfo is None or not now - timedelta(hours=24) <= stamp <= now + timedelta(minutes=5):
        raise ValueError("SQL evidence is stale or future dated")
    return stamp


def _compatible_type(expected, observed, name=None):
    match = re.fullmatch(r"([a-z0-9]+)(?:\((max|[0-9]+)(?:,\s*([0-9]+))?\))?", expected)
    if not match or observed["type"].lower() != match[1]:
        return False
    if match[2] is None:
        return True
    if match[1] in ("nvarchar", "nchar"):
        if match[2] == "max":
            if observed["max_length_bytes"] == -1:
                return True
            allowed = EXPLICIT_LENGTHS.get(name, LEGACY_FINITE_LENGTHS.get(name))
            return allowed is not None and observed["max_length_bytes"] == 2 * allowed
        return observed["max_length_bytes"] == (-1 if match[2] == "max" else 2 * int(match[2]))
    if match[1] in ("varchar", "char", "varbinary", "binary"):
        return observed["max_length_bytes"] == (-1 if match[2] == "max" else int(match[2]))
    if match[1] in ("decimal", "numeric"):
        return observed["numeric_precision"] == int(match[2]) and observed["numeric_scale"] == int(match[3])
    if match[1] in ("datetimeoffset", "datetime2", "time"):
        return observed["numeric_scale"] == int(match[2])
    return True


def validate(snapshot_raw, expected_raw, review, artifact, release, *, now=None):
    now = now or datetime.now(timezone.utc)
    if not SHA.fullmatch(artifact) or not re.fullmatch(r"[0-9a-f]{40}", release):
        raise ValueError("Exact artifact and release identity required")
    if release == BASELINE_RELEASE and artifact != BASELINE_MANIFEST:
        raise ValueError("Verified baseline release manifest mismatch")
    if review.get("mode") != "manual" or review.get("artifact_sha256") != artifact or review.get("release_commit") != release:
        raise ValueError("Manual SQL review is not bound to this release")
    if hashlib.sha256(snapshot_raw).hexdigest() != review.get("snapshot_sha256"):
        raise ValueError("SQL snapshot checksum mismatch")
    if hashlib.sha256(expected_raw).hexdigest() != review.get("expected_schema_sha256"):
        raise ValueError("Release schema checksum mismatch")
    reviewed = _time(review["reviewed_at"], now)
    if not re.fullmatch(r"[A-Za-z0-9_.-]{3,80}", review["reviewed_by"]):
        raise ValueError("Named SQL reviewer required")
    if not re.fullmatch(r"[A-Za-z0-9_./:-]{12,200}", review["record_reference"]):
        raise ValueError("Private SQL review reference required")
    if review.get("approval") != "APPROVE POS PRODUCTION SQL PREFLIGHT":
        raise ValueError("Explicit manual SQL review approval required")
    snapshot = json.loads(snapshot_raw)
    expected = json.loads(expected_raw)
    if release == BASELINE_RELEASE and (len(expected["columns"]) != 202 or len(expected["indexes"]) != 26
                                        or len(expected["mappedPrimaryKeys"]) != 24
                                        or len(expected["mappedForeignKeys"]) != 16):
        raise ValueError("Baseline release schema requirements incomplete")
    checked = _time(snapshot["checked_at"], now)
    if reviewed < checked:
        raise ValueError("SQL review predates the query")
    if snapshot["database"] != "anisedatabase" or review["database"] != "anisedatabase":
        raise ValueError("Production SQL database identity mismatch")
    if review["connection_host"] != "mssqluk20.prosql.net" or not snapshot["server"]:
        raise ValueError("Production SQL server identity incomplete")
    if review["observed_server"] != snapshot["server"]:
        raise ValueError("SQL server observation mismatch")
    columns = {f"{x['schema']}.{x['table']}.{x['column']}": x for x in snapshot["columns"]}
    indexes = {f"{x['schema']}.{x['table']}.{x['name']}": x for x in snapshot["indexes"]}
    if len(columns) != len(snapshot["columns"]) or len(indexes) != len(snapshot["indexes"]):
        raise ValueError("Duplicate SQL metadata entries")
    for name in expected["columns"]:
        if name not in columns:
            raise ValueError("Missing EF mapped column: " + name)
    if len(expected["mappedTypes"]) != len(expected["columns"]):
        raise ValueError("Incomplete release column type requirements")
    for item in expected["mappedTypes"]:
        observed = columns[item["name"]]
        if not item["type"] or not _compatible_type(item["type"], observed, item["name"]) or observed["nullable"] is not item["nullable"]:
            raise ValueError("Incompatible EF mapped column: " + item["name"])
    if len(expected["mappedIndexes"]) != len(expected["indexes"]):
        raise ValueError("Incomplete release index requirements")
    for item in expected["mappedIndexes"]:
        table = item["name"].split(".")[1]
        candidates = [x for x in snapshot["indexes"] if x["table"] == table and
                      [k["name"] for k in x["key_rows"]] == item["keys"]]
        # Convention-created nonunique EF indexes are performance aids; older
        # SQL scripts did not create every one. Unique semantics are required.
        if item["unique"] and not any(x["unique"] is True for x in candidates):
            raise ValueError("Incompatible EF mapped index: " + item["name"])
    for item in expected["mappedPrimaryKeys"]:
        if not item["keys"] or not any(x["table"] == item["table"] and x["primary_key"] is True and
                                        [k["name"] for k in x["key_rows"]] == item["keys"]
                                        for x in snapshot["indexes"]):
            raise ValueError("Missing EF mapped primary key: " + item["table"])
    for name, (kind, nullable) in REQUIRED_COLUMNS.items():
        item = columns.get(name)
        if item is None or item["type"].lower() != kind or item["nullable"] is not nullable:
            raise ValueError("Missing or incompatible startup/migration column: " + name)
    for name, length in EXPLICIT_LENGTHS.items():
        if name not in columns or columns[name]["max_length_bytes"] != length * 2:
            raise ValueError("Explicit SQL column length mismatch: " + name)
    for name, unique in REQUIRED_INDEXES.items():
        item = indexes.get(name)
        if (item is None or item["unique"] is not unique
                or [x["name"] for x in item["key_rows"]] != REQUIRED_INDEX_KEYS[name]):
            raise ValueError("Missing or incompatible migration index: " + name)
    payment = indexes["dbo.Payments.UX_Payments_StripeCheckout_Reference"]
    if [x["name"] for x in payment["key_rows"]] != ["Provider", "ProviderReference"] or "StripeCheckout" not in (payment.get("filter") or ""):
        raise ValueError("Stripe payment index definition mismatch")
    cash = indexes["dbo.Payments.UX_Payments_WebsiteCash_Reference"]
    if [x["name"] for x in cash["key_rows"]] != ["Provider", "ProviderReference"] or "WebsiteCash" not in (cash.get("filter") or ""):
        raise ValueError("Website cash index definition mismatch")
    foreign_keys = snapshot["foreign_keys"]
    for item in expected["mappedForeignKeys"]:
        if not any(all(observed[k] == v for k, v in item.items()) for observed in foreign_keys):
            raise ValueError("Missing EF mapped foreign key")
    if not any(x["parent_table"] == "Refunds" and x["parent_column"] == "OrderId" and
               x["referenced_table"] == "Orders" and x["referenced_column"] == "Id"
               for x in foreign_keys):
        raise ValueError("Refund order foreign key missing")
    if not isinstance(snapshot["check_constraints"], list):
        raise ValueError("Check constraints unavailable")
    for name, required in REQUIRED_DEFAULTS.items():
        value = columns.get(name, {}).get("default_definition")
        if value is None or re.sub(r"[\s()]", "", value).strip("'").lower() != required:
            raise ValueError("Required default constraint missing: " + name)
    for table, definitions in REQUIRED_CHECKS.items():
        actual = [re.sub(r"[\s\[\]()]", "", x["definition"]).lower()
                  for x in snapshot["check_constraints"] if x["table"] == table]
        for parts in definitions:
            if not any(all(part in definition for part in parts) for definition in actual):
                raise ValueError("Required check constraint missing: " + table)
    history = snapshot["migration_history"]
    if history["available"] is True:
        recorded = {x["version"]: x["checksum"] for x in history["records"]}
        for version, checksum in review["migration_checksums"].items():
            if recorded.get(version) != checksum:
                raise ValueError("Recorded migration checksum mismatch: " + version)
    elif history["available"] is not False or history["records"]:
        raise ValueError("Inconsistent migration history evidence")
    for version in ("010_order_lifecycle.sql", "011_website_stripe_reconciliation.sql"):
        recorded_checksum = review["migration_checksums"][version]
        if not SHA.fullmatch(recorded_checksum):
            raise ValueError("Migration checksum evidence missing")
        checked_in = json.loads((Path(__file__).resolve().parents[2] / "database/migration-checksums.json").read_text())[version]
        if recorded_checksum != checked_in:
            raise ValueError("Migration checksum differs from release source")
    if review.get("pending_migrations") != []:
        raise ValueError("Pending SQL migration review required")
    return True
