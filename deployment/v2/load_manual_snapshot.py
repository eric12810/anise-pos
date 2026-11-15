"""Expand a protected metadata-only SQL snapshot secret on an ephemeral runner."""
import base64
import json
import os
from pathlib import Path
import zlib


def load(encoded, destination):
    if not encoded or len(encoded) > 48000:
        raise ValueError("Protected SQL snapshot secret missing or oversized")
    packed = base64.b64decode(encoded, validate=True)
    decompressor = zlib.decompressobj()
    raw = decompressor.decompress(packed, 1_000_001)
    if len(raw) > 1_000_000 or not decompressor.eof or decompressor.unused_data:
        raise ValueError("Invalid or oversized SQL snapshot")
    snapshot = json.loads(raw)
    if not isinstance(snapshot, dict) or snapshot.get("database") != "anisedatabase":
        raise ValueError("Production SQL snapshot identity missing")
    path = Path(destination)
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "wb") as output:
        output.write(raw)


if __name__ == "__main__":
    load(os.environ["V2_PRODUCTION_SQL_SNAPSHOT_ZLIB_B64"], "sql-snapshot.json")
