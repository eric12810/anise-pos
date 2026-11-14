"""Guarded application-only rollback to the last verified healthy POS manifest."""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import time
from urllib.request import urlopen
import zipfile

spec = importlib.util.spec_from_file_location('uploader', Path(__file__).with_name('upload-staging.py'))
uploader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(uploader)

RUN = 43576579126
OLD_SHA = '064bcd606d6160edfb1bbba3b63d4f545783eac9c32be5e8d522243952f91efd'
CURRENT_SHA = '6f043d3b049828a37131056be011e5e9e34e4843e87c46639aa1088285804416'
PACKAGE_SHA = '3b573752b907f970dc03691bf1198c4eda2cfa7cd6eb51a603455a4ea84b12c5'
PACKAGE = Path('release-preparation/rollback-43576579126.zip')
ADDED = {'wwwroot/end-of-day-print.js', 'wwwroot/end-of-day-print.js.br', 'wwwroot/end-of-day-print.js.gz'}
MARKER = uploader.DEPLOYED_MANIFEST
PENDING = uploader.PENDING_MANIFEST
PREVIOUS = uploader.BACKUP_MANIFEST
MAINTENANCE = 'app_offline.htm'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def get(ftp, name):
    result = io.BytesIO()
    ftp.retrbinary('RETR ' + name, result.write)
    return result.getvalue()


def parse_manifest(raw, count):
    payload = json.loads(raw)
    files = payload.get('files')
    if (payload.get('version') != 1 or payload.get('environment') != 'production'
            or payload.get('application') != 'pos.loveanise.co.uk'
            or not isinstance(files, dict) or len(files) != count):
        raise RuntimeError('Manifest identity or file count differs from approved rollback')
    if any(not isinstance(name, str) or not uploader.SAFE_MANIFEST_PATH.fullmatch(name)
           or '..' in Path(name).parts or not isinstance(value, str)
           or not uploader.SHA256_HEX.fullmatch(value) for name, value in files.items()):
        raise RuntimeError('Manifest contains an invalid path or digest')
    return files


def load_package():
    if digest(PACKAGE.read_bytes()) != PACKAGE_SHA:
        raise RuntimeError('Rollback package digest changed')
    with zipfile.ZipFile(PACKAGE) as archive:
        names = archive.namelist()
        raw = archive.read('original-manifest.json')
        if digest(raw) != OLD_SHA:
            raise RuntimeError('Original manifest digest changed')
        old = parse_manifest(raw, 122)
        files = {name[6:]: archive.read(name) for name in names if name.startswith('files/')}
        if len(names) != 65 or len(files) != 64 or any(digest(data) != old.get(name) for name, data in files.items()):
            raise RuntimeError('Rollback file set or hashes differ from original manifest')
    return raw, old, files


def connect():
    if uploader.DEPLOYMENT_ENV != 'production':
        raise RuntimeError('Production-only rollback refused in another environment')
    host, user, password, root = (os.environ[k] for k in
                                  ('LIVE_FTPS_HOST', 'LIVE_FTPS_USERNAME', 'LIVE_FTPS_PASSWORD', 'PROD_FTPS_ROOT'))
    return uploader.CurlFTP(host, user, password, root, 'liveposftp', '/wwwroot')


def check_current(ftp, old, payloads):
    ftp.cwd('/wwwroot')
    root_names = set(ftp.nlst())
    if {MARKER, PENDING, PREVIOUS, MAINTENANCE} & root_names != {MARKER}:
        raise RuntimeError('Production deployment markers changed; rollback refused')
    raw = get(ftp, MARKER)
    if digest(raw) != CURRENT_SHA:
        raise RuntimeError('Current manifest changed; rollback refused')
    current = parse_manifest(raw, 125)
    changed = {name for name in old if old[name] != current.get(name)}
    if changed != set(payloads) or set(current) - set(old) != ADDED or set(old) - set(current):
        raise RuntimeError('Production manifest difference is outside approved rollback')
    for name, expected in current.items():
        if digest(get(ftp, name)) != expected:
            raise RuntimeError('Current production file differs: ' + name)
    print('PASS: 125 current managed production files match the failed-release manifest', flush=True)
    return raw, current


def backup(ftp, current_raw, current):
    path = Path('production-current-managed-backup.zip')
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        archive.writestr('current-manifest.json', current_raw)
        for name, expected in current.items():
            data = get(ftp, name)
            if digest(data) != expected:
                raise RuntimeError('Current file changed during backup: ' + name)
            archive.writestr('files/' + name, data)
    print('PASS: complete current managed application package backed up for forward recovery', flush=True)


def check_health():
    last = 'no response'
    for attempt in range(18):
        try:
            with urlopen('https://pos.loveanise.co.uk/health', timeout=20) as response:
                body = json.load(response)
                if response.status == 200 and body.get('status') == 'ok' and body.get('database') == 'ok':
                    return
                last = 'unexpected health content'
        except Exception as error:
            last = type(error).__name__
        if attempt < 17:
            time.sleep(10)
    raise RuntimeError('POS health did not become OK within retry window: ' + last)


def check_public_routes():
    for path in ('/api/menu', '/staff-login.html'):
        with urlopen('https://pos.loveanise.co.uk' + path, timeout=20) as response:
            if response.status != 200:
                raise RuntimeError('Post-rollback route failed: ' + path)
    print('PASS: menu and staff login page respond', flush=True)


def restore(ftp, old_raw, old, payloads):
    ftp.cwd('/wwwroot')
    ftp.storbinary('STOR ' + PENDING, io.BytesIO(old_raw))
    if get(ftp, PENDING) != old_raw:
        raise RuntimeError('Rollback pending manifest verification failed')
    ftp.storbinary('STOR ' + MAINTENANCE, io.BytesIO(uploader.MAINTENANCE))
    time.sleep(15)
    for index, (name, data) in enumerate(sorted(payloads.items()), 1):
        ftp.storbinary('STOR ' + name, io.BytesIO(data))
        if digest(get(ftp, name)) != old[name]:
            raise RuntimeError('Rollback upload verification failed: ' + name)
        print(f'Verified rollback file {index}/64: {name}', flush=True)
    for name in sorted(ADDED):
        ftp.delete(name)
    for name, expected in old.items():
        if digest(get(ftp, name)) != expected:
            raise RuntimeError('Restored application differs from original manifest: ' + name)
    for name in ADDED:
        try:
            get(ftp, name)
        except RuntimeError:
            continue
        raise RuntimeError('Failed-release-only file remains: ' + name)
    print('PASS: all 122 old application files restored and verified; maintenance remains', flush=True)
    ftp.delete(MAINTENANCE)
    try:
        check_health()
        check_public_routes()
    except Exception:
        ftp.storbinary('STOR ' + MAINTENANCE, io.BytesIO(uploader.MAINTENANCE))
        raise
    ftp.rename(MARKER, PREVIOUS)
    try:
        ftp.rename(PENDING, MARKER)
    except Exception:
        ftp.rename(PREVIOUS, MARKER)
        raise
    if digest(get(ftp, MARKER)) != OLD_SHA:
        raise RuntimeError('Active rollback manifest verification failed')
    ftp.delete(PREVIOUS)
    if {MARKER, PENDING, PREVIOUS, MAINTENANCE} & set(ftp.nlst()) != {MARKER}:
        raise RuntimeError('Final rollback marker state needs manual review')
    print('PASS: previous POS application healthy; original manifest active; maintenance removed', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['preflight', 'restore'])
    parser.add_argument('--approval', default='')
    args = parser.parse_args()
    if args.mode == 'restore' and args.approval != f'ROLLBACK POS RELEASE {RUN}':
        raise RuntimeError('Explicit rollback approval text required')
    old_raw, old, payloads = load_package()
    with connect() as ftp:
        current_raw, current = check_current(ftp, old, payloads)
        if args.mode == 'preflight':
            backup(ftp, current_raw, current)
        else:
            restore(ftp, old_raw, old, payloads)


if __name__ == '__main__':
    main()
