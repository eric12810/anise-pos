"""Explicit FTPS staging upload. Default mode only checks the connection."""
import argparse
import ftplib
import hashlib
import io
import os
from pathlib import Path, PurePosixPath
import ssl
import time
import subprocess
import tempfile
from urllib.parse import quote

HOST = 'wsuk32.pronameserver.com'
USER = 'stageposftp'
REMOTE_ROOT = '/stageposftp/wwwroot'
CONFIRMATION = 'DEPLOY pos-staging.loveanise.co.uk'


MAINTENANCE = b'<html><body>Staging maintenance</body></html>'


class StagingFTP(ftplib.FTP_TLS):
    def storbinary(self, command, stream):
        # curl closes IIS encrypted upload connections without ftplib's unwrap wait.
        if not command.startswith('STOR '):
            raise ValueError('Only file uploads are supported')
        remote = self.pwd().rstrip('/') + '/' + command[5:]
        if not remote.startswith(REMOTE_ROOT + '/'):
            raise ValueError('Upload outside staging wwwroot')
        credentials = USER + ':' + os.environ['STAGING_FTPS_PASSWORD']
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


def upload(ftp, files):
    ftp.cwd(REMOTE_ROOT)
    names = {name.lower() for name in directory_names(ftp)}
    if 'app_offline.htm' in names:
        marker = io.BytesIO()
        ftp.retrbinary('RETR app_offline.htm', marker.write)
        if marker.getvalue() != MAINTENANCE:
            raise RuntimeError('Unrecognized maintenance file; inspect before retrying.')
        print('Resuming staging upload with verified maintenance marker.')
    preserve_config = 'web.config' in names
    ftp.storbinary('STOR app_offline.htm', io.BytesIO(MAINTENANCE))
    # IIS needs time to release loaded assemblies. On failure leave the site offline.
    time.sleep(15)
    for name, path, digest in files:
        if name.lower() == 'web.config' and preserve_config:
            print('Preserving existing web.config')
            continue
        ftp.cwd(REMOTE_ROOT)
        for part in PurePosixPath(name).parent.parts:
            children = directory_names(ftp)
            if part not in children:
                ftp.mkd(part)
            ftp.cwd(part)
        with path.open('rb') as stream:
            ftp.storbinary('STOR ' + path.name, stream)
        received = hashlib.sha256()
        ftp.retrbinary('RETR ' + path.name, received.update)
        if received.hexdigest() != digest:
            raise RuntimeError('Remote file verification failed; site remains offline.')
        print('Verified: ' + name)
    ftp.cwd(REMOTE_ROOT)
    # This is the only deletion: the temporary marker created by this run.
    ftp.delete('app_offline.htm')
    print('Staging files uploaded and verified; maintenance marker removed.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--deploy', action='store_true')
    args = parser.parse_args()
    if args.deploy and os.environ.get('DEPLOY_CONFIRMATION') != CONFIRMATION:
        raise ValueError('Explicit deployment confirmation is required')
    if os.environ.get('STAGING_FTPS_HOST') != HOST or os.environ.get('STAGING_FTPS_USERNAME') != USER:
        raise ValueError('Credentials must match the approved staging host and account')
    password = os.environ.get('STAGING_FTPS_PASSWORD')
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
