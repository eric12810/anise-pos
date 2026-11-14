"""Read-only curl and FTP_TLS probes of the production POS FTP home."""
import ftplib
import os
from pathlib import PurePosixPath
import re
import ssl
import subprocess
from urllib.parse import quote

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
    return values


def safe_names(values):
    names = {PurePosixPath(value).name for value in values if value}
    if any('/' in name or '\\' in name or not name.isprintable() for name in names):
        raise RuntimeError('Directory listing contained an unsafe filename')
    return names


def curl_list(config, path):
    # Credentials go through curl's stdin config, never command arguments or logs.
    credentials = config['LIVE_FTPS_USERNAME'] + ':' + config['LIVE_FTPS_PASSWORD']
    escaped = credentials.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\r', '\\r')
    url = 'ftp://' + config['LIVE_FTPS_HOST'] + ':21/' + quote(path.strip('/'), safe='/')
    if not url.endswith('/'):
        url += '/'
    result = subprocess.run([
        'curl', '--silent', '--show-error', '--ssl-reqd', '--proto', '=ftp',
        '--ftp-method', 'multicwd', '--list-only', '--connect-timeout', '20',
        '--max-time', '45', '--config', '-', url,
    ], input='user = "' + escaped + '"\n', text=True, capture_output=True, check=False)
    if result.returncode:
        status = re.search(r'\b(4\d\d|5\d\d)\b', result.stderr)
        if status and status.group(1) == '550':
            reason = 'FTP 550 (directory unavailable or denied)'
        elif result.returncode == 9:
            reason = 'FTP directory navigation denied (curl exit 9)'
        elif result.returncode == 67:
            reason = 'FTP authentication rejected'
        elif result.returncode in (35, 51, 60):
            reason = 'TLS negotiation or certificate verification failure'
        elif result.returncode in (18, 28, 56):
            reason = 'FTPS data-channel or timeout failure'
        else:
            reason = 'curl exit ' + str(result.returncode) + (', FTP ' + status.group(1) if status else '')
        print('curl ' + path + ': FAIL ' + reason)
        return None
    try:
        names = safe_names(result.stdout.splitlines())
    except RuntimeError:
        print('curl ' + path + ': FAIL unsafe listing')
        return None
    print('curl ' + path + ': PASS; filenames: ' + ', '.join(sorted(names)))
    return names


def python_probe(config, path):
    try:
        with ftplib.FTP_TLS(context=ssl.create_default_context(), timeout=45) as ftp:
            ftp.connect(config['LIVE_FTPS_HOST'], 21)
            ftp.auth()
            ftp.login(config['LIVE_FTPS_USERNAME'], config['LIVE_FTPS_PASSWORD'])
            ftp.prot_p()
            ftp.cwd(path)
            try:
                names = safe_names(name for name, _ in ftp.mlsd())
                method = 'MLSD'
            except ftplib.error_perm as error:
                if str(error)[:3] not in {'500', '502', '504'}:
                    raise
                print('Python ' + path + ': MLSD unsupported; trying NLST')
                names = safe_names(ftp.nlst())
                method = 'NLST'
            print('Python ' + path + ': PASS ' + method + '; filenames: ' + ', '.join(sorted(names)))
            return names
    except ftplib.error_perm as error:
        print('Python ' + path + ': FAIL FTP ' + str(error)[:3])
    except ftplib.all_errors:
        print('Python ' + path + ': FAIL TLS, navigation, or data channel')
    return None


def main():
    config = required_config()
    print('PASS: protected account is liveposftp; TLS certificate verification required')
    configured = config['PROD_FTPS_ROOT'] == EXPECTED_ROOT
    print('Configured root: ' + ('PASS /wwwroot' if configured else 'FAIL differs from /wwwroot'))
    root = curl_list(config, '/')
    site = curl_list(config, EXPECTED_ROOT)
    python_probe(config, '/')
    python_probe(config, EXPECTED_ROOT)
    if root is not None and not root:
        print('FTP directory state: root listing empty; direct /wwwroot probe was still attempted')
    if site is None or not REQUIRED_FILES.issubset(site):
        raise RuntimeError('curl did not verify both required production POS filenames in /wwwroot')
    if not configured:
        raise RuntimeError('PROD_FTPS_ROOT differs from the verified /wwwroot destination')
    print('PASS: curl verified the production POS /wwwroot destination; Python comparison complete')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        print('FAIL: ' + str(error))
        raise SystemExit(1)
