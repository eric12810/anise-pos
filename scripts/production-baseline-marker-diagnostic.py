"""Read-only diagnostic for production baseline marker state."""
import ftplib
import hashlib
import io
import json
import os
from pathlib import PurePosixPath
import ssl
from urllib.request import urlopen

APPLICATION = 'pos.loveanise.co.uk'
MANIFEST = '.anise-pos-production-deployment.json'
PENDING = MANIFEST + '.pending'
PREVIOUS = MANIFEST + '.previous'
MAINTENANCE = 'app_offline.htm'
MARKERS = (MANIFEST, PENDING, PREVIOUS, MAINTENANCE)


def validate_manifest(data):
    try:
        payload = json.loads(data.decode('utf-8'))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        return False, 'invalid UTF-8 JSON'
    files = payload.get('files')
    if (payload.get('version') != 1 or payload.get('environment') != 'production'
            or payload.get('application') != APPLICATION or not isinstance(files, dict) or not files):
        return False, 'incorrect production identity or format'
    if any(not isinstance(name, str) or not isinstance(digest, str)
           or len(digest) != hashlib.sha256().digest_size * 2
           or any(character not in '0123456789abcdefABCDEF' for character in digest)
           for name, digest in files.items()):
        return False, 'invalid managed-file hash format'
    return True, f'valid production baseline with {len(files)} managed files'


def root_names(ftp, root):
    ftp.cwd(root)
    try:
        return {PurePosixPath(name).name for name, _ in ftp.mlsd()}
    except ftplib.error_perm as error:
        if str(error)[:3] not in {'500', '502', '504'}:
            raise RuntimeError('Production root listing failed') from error
        return {PurePosixPath(name).name for name in ftp.nlst()}


def marker_state(ftp, root):
    present = tuple(marker for marker in MARKERS if marker in root_names(ftp, root))
    result = {'present': present, 'baseline': 'absent', 'recovery': ()}
    if MANIFEST in present:
        data = io.BytesIO()
        ftp.cwd(root)
        ftp.retrbinary('RETR ' + MANIFEST, data.write)
        valid, detail = validate_manifest(data.getvalue())
        result['baseline'] = detail if valid else 'invalid manifest: ' + detail
    result['recovery'] = tuple(marker for marker in present if marker != MANIFEST)
    return result


def check_health(opener=urlopen):
    with opener('https://' + APPLICATION + '/health', timeout=20) as response:
        return response.status == 200


def required_config():
    values = {name: os.environ.get(name, '') for name in (
        'LIVE_FTPS_HOST', 'LIVE_FTPS_USERNAME', 'LIVE_FTPS_PASSWORD', 'PROD_FTPS_ROOT')}
    missing = [name for name, value in values.items() if not value]
    if missing:
        raise RuntimeError('Missing production FTPS configuration: ' + ', '.join(missing))
    if 'staging' in values['PROD_FTPS_ROOT'].lower():
        raise RuntimeError('Configured production FTPS root is a staging directory')
    return values


def main():
    config = required_config()
    with ftplib.FTP_TLS(context=ssl.create_default_context(), timeout=60) as ftp:
        ftp.connect(config['LIVE_FTPS_HOST'], 21)
        ftp.auth()
        ftp.login(config['LIVE_FTPS_USERNAME'], config['LIVE_FTPS_PASSWORD'])
        ftp.prot_p()
        ftp.set_pasv(True)
        state = marker_state(ftp, config['PROD_FTPS_ROOT'])
    print('Protected production markers present: ' + (', '.join(state['present']) if state['present'] else 'none'))
    print('Baseline status: ' + state['baseline'])
    if state['recovery']:
        print('Recovery required: investigate the listed marker state with the hosting/deployment owner; no marker was changed')
    elif state['baseline'].startswith('valid production baseline'):
        print('Baseline already exists; do not bootstrap or replace it')
    else:
        print('No protected marker requires recovery investigation')
    if not check_health():
        raise RuntimeError('Production POS health endpoint did not return HTTP 200')
    print('Production POS health: HTTP 200 (read-only)')
    print('READ-ONLY MARKER DIAGNOSTIC COMPLETE: no files were created, changed, renamed, deleted, or published')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Production baseline marker diagnostic failed: ' + str(error))
        raise SystemExit(1)
