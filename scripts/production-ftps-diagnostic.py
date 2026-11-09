"""Read-only production FTPS and deployment-baseline diagnostic."""
import ftplib
import io
import json
import os
from pathlib import PurePosixPath
import ssl

MANIFEST = '.anise-pos-production-deployment.json'
PENDING = MANIFEST + '.pending'
PREVIOUS = MANIFEST + '.previous'
APPLICATION = 'pos.loveanise.co.uk'


def required_config():
    values = {
        'LIVE_FTPS_HOST': os.environ.get('LIVE_FTPS_HOST', ''),
        'LIVE_FTPS_USERNAME': os.environ.get('LIVE_FTPS_USERNAME', ''),
        'LIVE_FTPS_PASSWORD': os.environ.get('LIVE_FTPS_PASSWORD', ''),
        'PROD_FTPS_ROOT': os.environ.get('PROD_FTPS_ROOT', ''),
    }
    missing = [name for name, value in values.items() if not value]
    if missing:
        raise RuntimeError('Missing production FTPS configuration: ' + ', '.join(missing))
    if 'staging' in values['PROD_FTPS_ROOT'].lower():
        raise RuntimeError('Configured production FTPS root is a staging directory')
    return values


def names(ftp):
    try:
        return {name for name, _ in ftp.mlsd()}
    except ftplib.error_perm as error:
        if str(error)[:3] not in {'500', '502', '504'}:
            raise RuntimeError('Production directory listing failed') from error
        return {PurePosixPath(name).name for name in ftp.nlst()}


def check_manifest(ftp, root_names):
    if 'app_offline.htm' in {name.lower() for name in root_names}:
        raise RuntimeError('Incomplete deployment: app_offline.htm is present')
    if PENDING in root_names or PREVIOUS in root_names:
        raise RuntimeError('Incomplete deployment: pending or previous manifest is present')
    if MANIFEST not in root_names:
        raise RuntimeError('Missing verified production deployment baseline manifest')
    data = io.BytesIO()
    ftp.retrbinary('RETR ' + MANIFEST, data.write)
    try:
        payload = json.loads(data.getvalue().decode('utf-8'))
        if (payload.get('version') != 1 or payload.get('environment') != 'production'
                or payload.get('application') != APPLICATION
                or not isinstance(payload.get('files'), dict)
                or not payload['files']):
            raise ValueError
    except (UnicodeDecodeError, ValueError, TypeError, KeyError):
        raise RuntimeError('Production deployment baseline manifest is malformed or has incorrect identity')
    print('PASS: verified production deployment baseline identifies ' + APPLICATION)


def main():
    config = required_config()
    context = ssl.create_default_context()
    try:
        with ftplib.FTP_TLS(context=context, timeout=60) as ftp:
            print('PASS: TLS connection established with certificate verification enabled')
            ftp.connect(config['LIVE_FTPS_HOST'], 21)
            ftp.auth()
            ftp.login(config['LIVE_FTPS_USERNAME'], config['LIVE_FTPS_PASSWORD'])
            print('PASS: FTPS authentication succeeded')
            ftp.prot_p()
            ftp.cwd(config['PROD_FTPS_ROOT'])
            root_names = names(ftp)
            print('PASS: configured production directory exists and is listable')
            check_manifest(ftp, root_names)
    except RuntimeError:
        raise
    except ftplib.error_perm as error:
        raise RuntimeError('Production FTPS authentication or directory access failed') from error
    except (OSError, ssl.SSLError) as error:
        raise RuntimeError('Production FTPS connection or TLS verification failed') from error
    print('READ-ONLY DIAGNOSTIC PASSED: no files were created, changed, renamed, or deleted')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Production FTPS diagnostic failed: ' + str(error))
        raise SystemExit(1)
