"""Read-only verification of an interrupted POS release against its exact artifact."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path

spec = importlib.util.spec_from_file_location('uploader', Path(__file__).with_name('upload-staging.py'))
uploader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(uploader)


def read(ftp, name):
    data = io.BytesIO()
    ftp.retrbinary('RETR ' + name, data.write)
    return data.getvalue()


def validate(data, label):
    payload = json.loads(data.decode('utf-8'))
    files = payload.get('files')
    if (payload.get('version') != 1 or payload.get('environment') != 'production'
            or payload.get('application') != 'pos.loveanise.co.uk'
            or not isinstance(files, dict) or not files):
        raise RuntimeError(label + ' has invalid identity or format')
    for name, digest in files.items():
        if (not isinstance(name, str) or not uploader.SAFE_MANIFEST_PATH.fullmatch(name)
                or '..' in Path(name).parts or not isinstance(digest, str)
                or not uploader.SHA256_HEX.fullmatch(digest)):
            raise RuntimeError(label + ' has invalid file entries')
    return files


def main():
    if uploader.DEPLOYMENT_ENV != 'production':
        raise RuntimeError('Production read-only mode required')
    root = Path('artifacts/staging')
    artifact = uploader.package_files(root, root / 'release-manifest.sha256')
    host, user, password, path = (os.environ[k] for k in (
        'LIVE_FTPS_HOST', 'LIVE_FTPS_USERNAME', 'LIVE_FTPS_PASSWORD', 'PROD_FTPS_ROOT'))
    if path != '/wwwroot' or user != 'liveposftp':
        raise RuntimeError('Unexpected production FTPS destination')
    with uploader.CurlFTP(host, user, password, path, 'liveposftp', '/wwwroot') as ftp:
        ftp.cwd(path)
        names = set(ftp.nlst())
        markers = {uploader.DEPLOYED_MANIFEST, uploader.PENDING_MANIFEST,
                   uploader.BACKUP_MANIFEST, 'app_offline.htm'}
        present = names & markers
        expected_markers = {uploader.DEPLOYED_MANIFEST, uploader.PENDING_MANIFEST, 'app_offline.htm'}
        if present != expected_markers:
            raise RuntimeError('Production marker state differs: ' + ', '.join(sorted(present)))
        existing_raw = read(ftp, uploader.DEPLOYED_MANIFEST)
        pending_raw = read(ftp, uploader.PENDING_MANIFEST)
        existing = validate(existing_raw, 'existing manifest')
        pending = validate(pending_raw, 'pending manifest')
        desired = {name: digest for name, _, digest in artifact
                   if name != 'web.config' or 'web.config' not in names}
        missing_from_pending = sorted(set(desired) - set(pending))
        extra_in_pending = sorted(set(pending) - set(desired))
        different_in_pending = sorted(name for name in desired.keys() & pending.keys()
                                      if desired[name] != pending[name])
        missing_remote, mismatched_remote = [], []
        for name, digest in pending.items():
            try:
                actual = hashlib.sha256(read(ftp, name)).hexdigest()
            except RuntimeError:
                missing_remote.append(name)
                continue
            if actual != digest:
                mismatched_remote.append(name)
        report = {
            'release_run': 43576579126,
            'approved_staging_run': 43572186352,
            'artifact_files': len(artifact),
            'existing_manifest_files': len(existing),
            'pending_manifest_files': len(pending),
            'existing_manifest_sha256': hashlib.sha256(existing_raw).hexdigest(),
            'pending_manifest_sha256': hashlib.sha256(pending_raw).hexdigest(),
            'markers': sorted(present),
            'missing_from_pending': missing_from_pending,
            'extra_in_pending': extra_in_pending,
            'different_in_pending': different_in_pending,
            'missing_remote': missing_remote,
            'mismatched_remote': mismatched_remote,
        }
        Path('production-release-verification.json').write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(report, indent=2))
        if any(report[k] for k in ('missing_from_pending', 'extra_in_pending',
                                   'different_in_pending', 'missing_remote', 'mismatched_remote')):
            raise RuntimeError('Production release verification found differences')
        print('PASS: pending manifest and all managed production files match exact approved artifact')


if __name__ == '__main__':
    main()
