"""Read-only verification of the production POS FTP account and site folder."""
import ftplib
import os
from pathlib import PurePosixPath
import ssl

EXPECTED_USER = 'liveposftp'
EXPECTED_ROOT = '/wwwroot'
REQUIRED_FILES = {'web.config', 'AnisePOS.Api.dll'}


def required_config():
    values = {name: os.environ.get(name, '') for name in (
        'LIVE_FTPS_HOST', 'LIVE_FTPS_USERNAME', 'LIVE_FTPS_PASSWORD', 'PROD_FTPS_ROOT')}
    missing = [name for name, value in values.items() if not value]
    if missing:
        raise RuntimeError('Missing production FTPS configuration: ' + ', '.join(missing))
    if values['LIVE_FTPS_USERNAME'] != EXPECTED_USER:
        raise RuntimeError('Production FTPS account does not match the approved account')
    if values['PROD_FTPS_ROOT'] != EXPECTED_ROOT:
        raise RuntimeError('PROD_FTPS_ROOT does not match the approved /wwwroot destination')
    return values


def names(ftp):
    try:
        result = [name for name, _ in ftp.mlsd()]
    except ftplib.error_perm as error:
        if str(error)[:3] not in {'500', '502', '504'}:
            raise
        result = [PurePosixPath(name).name for name in ftp.nlst()]
    if any(not name or '/' in name or '\\' in name or not name.isprintable()
           for name in result):
        raise RuntimeError('Directory listing contained an unsafe filename')
    return set(result)


def main():
    config = required_config()
    try:
        with ftplib.FTP_TLS(context=ssl.create_default_context(), timeout=60) as ftp:
            ftp.connect(config['LIVE_FTPS_HOST'], 21)
            ftp.auth()
            ftp.login(config['LIVE_FTPS_USERNAME'], config['LIVE_FTPS_PASSWORD'])
            ftp.prot_p()
            print('PASS: explicit FTPS authenticated with verified TLS and encrypted data channel')
            print('PASS: account is liveposftp; PROD_FTPS_ROOT is /wwwroot')

            # IIS FTP may return an empty root listing despite allowing direct CWD.
            # Navigate exactly as the staging uploader does.
            ftp.cwd(config['PROD_FTPS_ROOT'])
            site_names = names(ftp)
            print('FTP /wwwroot filenames: ' + ', '.join(sorted(site_names)))
            if not REQUIRED_FILES.issubset(site_names):
                raise RuntimeError('Production POS application files are missing from /wwwroot')
    except RuntimeError:
        raise
    except ftplib.all_errors:
        raise RuntimeError('FTPS connection, TLS verification, or read-only listing failed') from None
    print('PASS: production POS deployment destination verified by directory listings only')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        print('FAIL: ' + str(error))
        raise SystemExit(1)
