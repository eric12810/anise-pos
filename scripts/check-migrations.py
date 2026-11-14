"""Offline migration preflight. Never connects to SQL Server or prints secrets."""
import hashlib, json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
directory = root / 'database'
manifest = json.loads((directory / 'migration-checksums.json').read_text())
files = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.glob('*.sql')}
if set(files) != set(manifest):
    raise SystemExit('migration manifest mismatch')
changed = sorted(name for name, digest in files.items() if manifest[name] != digest)
if changed:
    raise SystemExit('migration checksum mismatch: ' + ', '.join(changed))
for name in sorted(files):
    if any(x in (directory / name).read_text().upper() for x in ('DROP TABLE', 'TRUNCATE TABLE')):
        raise SystemExit(f'unsafe destructive statement found in {name}')
print('Migration preflight passed: ' + ', '.join(sorted(files)))
print('Database history and pending-state checks require the approved staging SQL operation; no database connection was attempted.')
