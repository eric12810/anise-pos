"""Offline production release preflight. Never opens a SQL or hosting connection."""
from pathlib import Path
import re, sys

root = Path(__file__).resolve().parents[1]
startup = (root / 'src/AnisePOS.Api/Program.cs').read_text()
sql = '\n'.join(p.read_text() for p in (root / 'database').glob('*.sql'))
if 'ExecuteSqlRawAsync' not in startup or not re.search(r'CREATE TABLE|ALTER TABLE|CREATE UNIQUE INDEX', startup + sql, re.I):
    raise SystemExit('Could not identify startup/schema changes; refusing production enablement')
for forbidden in ('Customers', 'Orders', 'Payments', 'StaffUsers'):
    if forbidden not in startup + sql:
        raise SystemExit(f'Schema review did not cover sensitive table {forbidden}')
print('OFFLINE PREFLIGHT OK: startup schema changes inspected; no SQL connection attempted.')
