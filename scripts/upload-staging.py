"""Explicit FTPS staging upload. Default mode only checks the connection."""
import argparse
import ftplib
import hashlib
import io
import os
from pathlib import Path, PurePosixPath
import ssl
import time

HOST = 'wsuk32.pronameserver.com'
USER = 'stageposftp'
CONFIRMATION = 'DEPLOY pos-staging.loveanise.co.uk'


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


def upload(ftp, files):
    # Require MLSD rather than guessing whether files exist after a permissions error.
    names = {name.lower() for name, _ in ftp.mlsd()}
    if 'app_offline.htm' in names:
        raise RuntimeError('Site already offline; inspect it before retrying.')
    preserve_config = 'web.config' in names
    ftp.storbinary('STOR app_offline.htm', io.BytesIO(b'<html><body>Staging maintenance</body></html>'))
    # IIS needs time to release loaded assemblies. On failure leave the site offline.
    time.sleep(15)
    for name, path, digest in files:
        if name.lower() == 'web.config' and preserve_config:
            print('Preserving existing web.config')
            continue
        ftp.cwd('/')
        for part in PurePosixPath(name).parent.parts:
            children = {n: facts for n, facts in ftp.mlsd()}
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
    ftp.cwd('/')
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
    with ftplib.FTP_TLS(context=ssl.create_default_context(), timeout=60) as ftp:
        ftp.connect(HOST, 21)
        ftp.auth()
        ftp.login(USER, password)
        ftp.prot_p()
        ftp.cwd('/')
        list(ftp.mlsd())
        print('Verified TLS connection and encrypted directory listing at staging account root.')
        if args.deploy:
            upload(ftp, files)
        else:
            print('Connection check only: no files created, changed or deleted.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Server replies can include private information; avoid dumping them to logs.
        print('Staging operation failed (' + type(error).__name__ + '). No automatic retry. If upload began, inspect app_offline.htm before restarting the site.')
        raise SystemExit(1)
