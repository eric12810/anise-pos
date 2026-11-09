"""Explicit FTPS staging upload. Default mode only checks the connection."""
import argparse
import ftplib
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import ssl
import re
import time
import subprocess
import tempfile
from urllib.parse import quote

HOST = os.environ.get('DEPLOY_FTPS_HOST', 'wsuk32.pronameserver.com')
USER = os.environ.get('DEPLOY_FTPS_USERNAME', 'stageposftp')
REMOTE_ROOT = os.environ.get('DEPLOY_FTPS_ROOT', '/stageposftp/wwwroot')
CONFIRMATION = os.environ.get('DEPLOY_CONFIRMATION_TEXT', 'DEPLOY pos-staging.loveanise.co.uk')


MAINTENANCE = b'<html><body>Staging maintenance</body></html>'
DEPLOYED_MANIFEST = '.anise-pos-staging-deployment.json'
PENDING_MANIFEST = DEPLOYED_MANIFEST + '.pending'
BACKUP_MANIFEST = DEPLOYED_MANIFEST + '.previous'
MANIFEST_VERSION = 1
SHA256_HEX = re.compile(r'[0-9a-f]{64}\Z')
SAFE_MANIFEST_PATH = re.compile(r'[A-Za-z0-9_./-]+\Z')


class StagingFTP(ftplib.FTP_TLS):
    def storbinary(self, command, stream):
        # curl closes IIS encrypted upload connections without ftplib's unwrap wait.
        if not command.startswith('STOR '):
            raise ValueError('Only file uploads are supported')
        remote = self.pwd().rstrip('/') + '/' + command[5:]
        if not remote.startswith(REMOTE_ROOT + '/'):
            raise ValueError('Upload outside staging wwwroot')
        credentials = USER + ':' + (os.environ.get('DEPLOY_FTPS_PASSWORD') or os.environ.get('STAGING_FTPS_PASSWORD', ''))
        escaped = credentials.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\r', '\\r')
        with tempfile.NamedTemporaryFile() as payload:
            while chunk := stream.read(65536):
                payload.write(chunk)
            payload.flush()
            result = subprocess.run([
                'curl', '--silent', '--show-error', '--fail', '--ssl-reqd',
                '--connect-timeout', '30', '--max-time', '180', '--config', '-',
                '--upload-file', payload.name,
                'ftp://' + HOST + ':21/' + quote(remote, safe='/'),
            ], input='user = "' + escaped + '"\n', text=True, capture_output=True)
            if result.returncode:
                raise RuntimeError('curl upload failed with exit code ' + str(result.returncode))
        return '226 Upload complete'


def package_files(root, manifest):
    files = []
    for line in manifest.read_text().splitlines():
        digest, name = line.split('  ', 1)
        relative = PurePosixPath(name)
        if relative.is_absolute() or '..' in relative.parts or any(c in name for c in '\r\n\\'):
            raise ValueError('Unsafe manifest path')
        path = root / name
        if path.is_symlink() or root.resolve() not in path.resolve().parents:
            raise ValueError('Unsafe package path')
        if name.lower().startswith('appsettings') or any(p.lower() in {'data', 'app_data'} for p in relative.parts):
            raise ValueError('Configuration or runtime data in package')
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError('Package hash mismatch')
        files.append((name, path, digest))
    if not files or not any(name == 'AnisePOS.Api.dll' for name, _, _ in files):
        raise ValueError('API package is missing')
    return files


def directory_names(ftp):
    try:
        return [name for name, _ in ftp.mlsd()]
    except ftplib.error_perm as error:
        if str(error)[:3] not in {'500', '502', '504'}:
            raise
        # Older IIS FTP servers may not implement MLSD. NLST lists names only.
        # Never treat permission errors or failed listings as an empty directory.
        print('MLSD unsupported; using NLST directory listing.', flush=True)
        return [PurePosixPath(name).name for name in ftp.nlst()]


def read_deployed_manifest(ftp, root_names):
    """Read the last COMPLETE upload, never use a partial upload as a baseline."""
    if DEPLOYED_MANIFEST not in root_names:
        print('No prior deployment manifest: first run will upload the complete approved package.', flush=True)
        return None
    data = io.BytesIO()
    ftp.retrbinary('RETR ' + DEPLOYED_MANIFEST, data.write)
    try:
        payload = json.loads(data.getvalue().decode('utf-8'))
        hashes = payload['files']
        if payload.get('version') != MANIFEST_VERSION or not isinstance(hashes, dict):
            raise ValueError('Unexpected manifest format')
        for name, digest in hashes.items():
            if (not isinstance(name, str) or not SAFE_MANIFEST_PATH.fullmatch(name)
                    or PurePosixPath(name).is_absolute() or '..' in PurePosixPath(name).parts
                    or not isinstance(digest, str) or not SHA256_HEX.fullmatch(digest)):
                raise ValueError('Invalid manifest file entry')
        return hashes
    except (UnicodeDecodeError, ValueError, KeyError, TypeError) as error:
        raise RuntimeError('Invalid remote deployment manifest. No changes made.') from error


def save_deployed_manifest(ftp, files, had_previous):
    """Publish manifest only after every changed file is uploaded AND verified."""
    content = json.dumps(
        {'version': MANIFEST_VERSION, 'files': files},
        separators=(',', ':'), sort_keys=True
    ).encode('utf-8')
    ftp.cwd(REMOTE_ROOT)
    ftp.storbinary('STOR ' + PENDING_MANIFEST, io.BytesIO(content))
    if had_previous:
        ftp.rename(DEPLOYED_MANIFEST, BACKUP_MANIFEST)
    try:
        ftp.rename(PENDING_MANIFEST, DEPLOYED_MANIFEST)
    except Exception:
        if had_previous:
            ftp.rename(BACKUP_MANIFEST, DEPLOYED_MANIFEST)
        raise
    if had_previous:
        ftp.delete(BACKUP_MANIFEST)


def upload(ftp, files):
    ftp.cwd(REMOTE_ROOT)
    names = set(directory_names(ftp))
    lower_names = {name.lower() for name in names}
    if 'app_offline.htm' in lower_names:
        # Failed or interrupted deployments must be investigated before retrying.
        # Continuing from an old successful manifest could leave a mixed release.
        raise RuntimeError('Maintenance marker already present; recovery review required.')
    if PENDING_MANIFEST in names or BACKUP_MANIFEST in names:
        raise RuntimeError('Incomplete manifest publication; recovery review required.')

    preserve_config = 'web.config' in lower_names
    approved = [(name, path, digest) for name, path, digest in files
                if not (name.lower() == 'web.config' and preserve_config)]
    desired = {name: digest for name, _, digest in approved}
    previous = read_deployed_manifest(ftp, names)
    changed = [(name, path, digest) for name, path, digest in approved
               if previous is None or previous.get(name) != digest
               or (name.lower() == 'web.config' and 'web.config' not in lower_names)]
    skipped = len(approved) - len(changed)
    removed = sorted(set(previous or {}) - set(desired))
    print(f'PACKAGE: {len(files)} files; {len(changed)} to upload; '
          f'{skipped} unchanged; {len(files)-len(approved)} config preserved.', flush=True)
    if removed:
        print(f'WARNING: {len(removed)} previously deployed package files not in the '
              'new package; remote copies are preserved. Review incompatible or removed '
              'dependencies before using this build.', flush=True)
        for name in removed[:30]:
            print('Preserved remote file: ' + name, flush=True)
        # A removed DLL or asset can still affect ASP.NET Core behavior.
        # Require explicit review rather than silently deploying an incompatible set.
        raise RuntimeError('Package file removals detected; review required before deployment.')
    if not changed:
        print(f'SUCCESS: 0 uploaded; {skipped} unchanged. No maintenance or IIS restart.', flush=True)
        return

    # Changing even one application assembly must happen while IIS is offline.
    # If any upload or verification fails, leave this marker in place for recovery.
    ftp.storbinary('STOR app_offline.htm', io.BytesIO(MAINTENANCE))
    time.sleep(15)
    for index, (name, path, digest) in enumerate(changed, 1):
        ftp.cwd(REMOTE_ROOT)
        for part in PurePosixPath(name).parent.parts:
            children = directory_names(ftp)
            if part not in children:
                ftp.mkd(part)
            ftp.cwd(part)
        print(f'Uploading [{index}/{len(changed)}] {name}', flush=True)
        with path.open('rb') as stream:
            ftp.storbinary('STOR ' + path.name, stream)
        received = hashlib.sha256()
        ftp.retrbinary('RETR ' + path.name, received.update)
        if received.hexdigest() != digest:
            raise RuntimeError('Remote file verification failed; site remains offline.')
        print('Verified: ' + name, flush=True)

    save_deployed_manifest(ftp, desired, had_previous=previous is not None)
    ftp.cwd(REMOTE_ROOT)
    ftp.delete('app_offline.htm')
    print(f'SUCCESS: {len(changed)} uploaded and verified; {skipped} unchanged; '
          'manifest saved; maintenance marker removed. No runtime data deleted.', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--deploy', action='store_true')
    args = parser.parse_args()
    if args.deploy and os.environ.get('DEPLOY_CONFIRMATION') != CONFIRMATION:
        raise ValueError('Explicit deployment confirmation is required')
    if (os.environ.get('DEPLOY_FTPS_HOST') or os.environ.get('STAGING_FTPS_HOST')) != HOST or (os.environ.get('DEPLOY_FTPS_USERNAME') or os.environ.get('STAGING_FTPS_USERNAME')) != USER:
        raise ValueError('Credentials must match the approved staging host and account')
    password = os.environ.get('DEPLOY_FTPS_PASSWORD') or os.environ.get('STAGING_FTPS_PASSWORD')
    if not password:
        raise ValueError('Staging password secret is missing')
    files = package_files(Path('artifacts/staging'), Path('artifacts/staging-manifest.sha256'))
    with StagingFTP(context=ssl.create_default_context(), timeout=60) as ftp:
        print('Step: connect to staging FTPS', flush=True)
        ftp.connect(HOST, 21)
        print('Step: negotiate and verify TLS', flush=True)
        ftp.auth()
        print('Step: authenticate staging account', flush=True)
        ftp.login(USER, password)
        print('Step: encrypt data channel', flush=True)
        ftp.prot_p()
        print('Step: select staging root', flush=True)
        ftp.cwd(REMOTE_ROOT)
        print('Step: list staging root', flush=True)
        directory_names(ftp)
        print('Verified TLS connection and encrypted directory listing at /stageposftp/wwwroot.')
        if args.deploy:
            upload(ftp, files)
        else:
            print('Connection check only: no files created, changed or deleted.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Log only the protocol status code, never raw server replies or credentials.
        code = str(error)[:3]
        if isinstance(error, ftplib.Error) and code.isdigit():
            print('FTP response code: ' + code)
        # Server replies can include private information; avoid dumping them to logs.
        print('Staging operation failed (' + type(error).__name__ + '). No automatic retry. If upload began, inspect app_offline.htm before restarting the site.')
        raise SystemExit(1)
