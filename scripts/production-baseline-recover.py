"""Explicitly approved, read-only-validated production baseline finalisation."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path

BASELINE_PATH = Path(__file__).with_name('production-baseline.py')
spec = importlib.util.spec_from_file_location('production_baseline', BASELINE_PATH)
baseline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(baseline)


def fail(message):
    raise SystemExit('Recovery refused: ' + message)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inventory', required=True)
    parser.add_argument('--evidence', required=True, help='New local path for an exact pending-manifest copy')
    parser.add_argument('--owner-approval', required=True)
    args = parser.parse_args()
    if args.owner_approval != 'FINALISE PRODUCTION BASELINE PENDING MANIFEST':
        fail('separate owner approval is required')
    if os.environ.get('PROD_BASELINE_RECOVERY_BACKUP_VERIFIED', '').lower() != 'true':
        fail('restorable application and SQL backup verification is required')
    app_ref = os.environ.get('PROD_BASELINE_APP_BACKUP_REFERENCE', '').strip()
    sql_ref = os.environ.get('PROD_BASELINE_SQL_BACKUP_REFERENCE', '').strip()
    if not app_ref or not sql_ref or app_ref == sql_ref:
        fail('distinct application and SQL backup references are required')
    report = json.loads(Path(args.inventory).read_text())
    expected = report.get('managed_remote_hashes', {})
    if report.get('application') != baseline.APPLICATION or len(expected) != 122:
        fail('inspection report identity or managed-file count is not verified')
    with baseline.connect() as ftp:
        root = os.environ['PROD_FTPS_ROOT']
        markers = baseline.read_root_markers(ftp, root)
        if markers != (baseline.PENDING,):
            fail('production root markers are not exactly pending-only: ' + ', '.join(markers or ('none',)))
        data = bytearray()
        ftp.cwd(root)
        ftp.retrbinary('RETR ' + baseline.PENDING, data.extend)
        pending_sha = hashlib.sha256(data).hexdigest()
        evidence = Path(args.evidence)
        if evidence.exists():
            fail('evidence path already exists; choose a new path')
        evidence.write_bytes(bytes(data))
        print('Preserved local pending-manifest evidence: ' + str(evidence))
        pending = json.loads(bytes(data).decode('utf-8'))
        if (pending.get('version') != 1 or pending.get('environment') != 'production' or
                pending.get('application') != baseline.APPLICATION or pending.get('files') != expected or
                pending.get('backup_references', {}).get('application') != app_ref or
                pending.get('backup_references', {}).get('sql') != sql_ref):
            fail('pending manifest identity or hashes do not match the inspection report')
        first, _ = baseline.remote_files(ftp, root, set(expected), reconnect=lambda: baseline._open_ftp(baseline.config()))
        if first != expected:
            fail('current production file hashes do not match the inspection report')
        print('Validated pending manifest SHA-256: ' + pending_sha)
        print('Validated 122 current production file hashes and backup references')
        ftp.rename(baseline.PENDING, baseline.MANIFEST)
        markers_after = baseline.read_root_markers(ftp, root)
        if markers_after != (baseline.MANIFEST,):
            fail('post-rename marker verification failed')
        print('Recovery complete: pending manifest finalised; no application files were changed')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Production baseline recovery failed: ' + str(error))
        raise SystemExit(1)
