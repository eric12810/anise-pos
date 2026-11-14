"""Finalise only the already verified production manifest publication."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path

spec = importlib.util.spec_from_file_location('uploader', Path(__file__).with_name('upload-staging.py'))
uploader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(uploader)

EXPECTED_EXISTING_SHA = '064bcd606d6160edfb1bbba3b63d4f545783eac9c32be5e8d522243952f91efd'
EXPECTED_PENDING_SHA = '6f043d3b049828a37131056be011e5e9e34e4843e87c46639aa1088285804416'
EXPECTED_MARKERS = {uploader.DEPLOYED_MANIFEST, uploader.PENDING_MANIFEST, 'app_offline.htm'}


def read(ftp, name):
    data = bytearray()
    ftp.retrbinary('RETR ' + name, data.extend)
    return bytes(data)


def markers(ftp):
    protected = EXPECTED_MARKERS | {uploader.BACKUP_MANIFEST}
    return set(ftp.nlst()) & protected


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--verification', required=True)
    parser.add_argument('--approval', required=True)
    args = parser.parse_args()
    if args.approval != 'FINALISE POS RELEASE 43576579126':
        raise RuntimeError('Separate explicit production finalisation approval is required')
    if uploader.DEPLOYMENT_ENV != 'production':
        raise RuntimeError('Production environment required')
    report = json.loads(Path(args.verification).read_text())
    if (report.get('release_run') != 43576579126
            or report.get('approved_staging_run') != 43572186352
            or report.get('pending_manifest_files') != 125
            or set(report.get('markers', [])) != EXPECTED_MARKERS
            or report.get('existing_manifest_sha256') != EXPECTED_EXISTING_SHA
            or report.get('pending_manifest_sha256') != EXPECTED_PENDING_SHA
            or any(report.get(key) for key in ('missing_from_pending', 'extra_in_pending',
                 'different_in_pending', 'missing_remote', 'mismatched_remote'))):
        raise RuntimeError('Fresh read-only verification did not match the approved recovery state')
    host, user, password, root = (os.environ[k] for k in (
        'LIVE_FTPS_HOST', 'LIVE_FTPS_USERNAME', 'LIVE_FTPS_PASSWORD', 'PROD_FTPS_ROOT'))
    if root != '/wwwroot' or user != 'liveposftp':
        raise RuntimeError('Unexpected production FTPS destination')
    with uploader.CurlFTP(host, user, password, root, 'liveposftp', '/wwwroot') as ftp:
        ftp.cwd(root)
        if markers(ftp) != EXPECTED_MARKERS:
            raise RuntimeError('Production markers changed since verification')
        if hashlib.sha256(read(ftp, uploader.DEPLOYED_MANIFEST)).hexdigest() != EXPECTED_EXISTING_SHA:
            raise RuntimeError('Existing production manifest changed since verification')
        pending = read(ftp, uploader.PENDING_MANIFEST)
        if hashlib.sha256(pending).hexdigest() != EXPECTED_PENDING_SHA:
            raise RuntimeError('Pending production manifest changed since verification')
        ftp.rename(uploader.DEPLOYED_MANIFEST, uploader.BACKUP_MANIFEST)
        if markers(ftp) != {uploader.BACKUP_MANIFEST, uploader.PENDING_MANIFEST, 'app_offline.htm'}:
            raise RuntimeError('First manifest rename needs manual review; maintenance remains')
        try:
            ftp.rename(uploader.PENDING_MANIFEST, uploader.DEPLOYED_MANIFEST)
        except Exception:
            if markers(ftp) == {uploader.BACKUP_MANIFEST, uploader.PENDING_MANIFEST, 'app_offline.htm'}:
                ftp.rename(uploader.BACKUP_MANIFEST, uploader.DEPLOYED_MANIFEST)
            raise
        if (markers(ftp) != {uploader.BACKUP_MANIFEST, uploader.DEPLOYED_MANIFEST, 'app_offline.htm'}
                or read(ftp, uploader.DEPLOYED_MANIFEST) != pending):
            raise RuntimeError('New active manifest needs manual review; maintenance remains')
        ftp.delete(uploader.BACKUP_MANIFEST)
        if markers(ftp) != {uploader.DEPLOYED_MANIFEST, 'app_offline.htm'}:
            raise RuntimeError('Backup manifest cleanup needs manual review; maintenance remains')
        ftp.delete('app_offline.htm')
        if markers(ftp) != {uploader.DEPLOYED_MANIFEST}:
            raise RuntimeError('Maintenance marker cleanup needs manual review')
        print('PASS: active manifest finalised; previous and maintenance markers removed; no application files uploaded')


if __name__ == '__main__':
    main()
