"""Read-only curl and FTP_TLS probes of the production POS FTP home."""
import ftplib
from collections import Counter
import json
import os
from pathlib import PurePosixPath
import re
import ssl
import subprocess
from urllib.parse import quote
from urllib.parse import urlparse
import xml.etree.ElementTree as ET

EXPECTED_USER = 'liveposftp'
EXPECTED_ROOT = '/wwwroot'
REQUIRED_FILES = {'web.config', 'AnisePOS.Api.dll'}
MANIFEST = '.anise-pos-production-deployment.json'
STRIPE_SETTINGS = {'WebsiteCheckout__StripeSessionsEnabled', 'WebsiteCheckout__StripeMode',
                   'Stripe__SecretKey', 'WebsiteCheckout__StripeWebhookSecret',
                   'WebsiteCheckout__StripeSuccessUrl', 'WebsiteCheckout__StripeCancelUrl'}


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


def curl_manifest(config):
    credentials = config['LIVE_FTPS_USERNAME'] + ':' + config['LIVE_FTPS_PASSWORD']
    escaped = credentials.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\r', '\\r')
    url = 'ftp://' + config['LIVE_FTPS_HOST'] + ':21/wwwroot/' + MANIFEST
    result = subprocess.run([
        'curl', '--silent', '--show-error', '--fail', '--ssl-reqd', '--proto', '=ftp',
        '--ftp-method', 'multicwd', '--connect-timeout', '20', '--max-time', '45',
        '--max-filesize', '1048576', '--config', '-', url,
    ], input=('user = "' + escaped + '"\n').encode(), capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError('Read-only production manifest retrieval failed (curl exit ' + str(result.returncode) + ')')
    try:
        payload = json.loads(result.stdout)
        files = payload['files']
        if (payload.get('version') != 1 or payload.get('environment') != 'production'
                or payload.get('application') != 'pos.loveanise.co.uk'
                or not isinstance(files, dict) or not files
                or any(not isinstance(name, str) or not isinstance(digest, str)
                       or not re.fullmatch('[0-9a-f]{64}', digest) for name, digest in files.items())):
            raise ValueError
    except (ValueError, KeyError, TypeError):
        raise RuntimeError('Production manifest identity or format is invalid') from None
    print('PASS: production deployment manifest identifies pos.loveanise.co.uk; ' + str(len(files)) + ' managed files')


def curl_web_config(config):
    credentials = config['LIVE_FTPS_USERNAME'] + ':' + config['LIVE_FTPS_PASSWORD']
    escaped = credentials.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\r', '\\r')
    result = subprocess.run([
        'curl', '--silent', '--show-error', '--fail', '--ssl-reqd', '--proto', '=ftp',
        '--ftp-method', 'multicwd', '--connect-timeout', '20', '--max-time', '45',
        '--max-filesize', '1048576', '--config', '-',
        'ftp://' + config['LIVE_FTPS_HOST'] + ':21/wwwroot/web.config',
    ], input=('user = "' + escaped + '"\n').encode(), capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError('Read-only web.config retrieval failed (curl exit ' + str(result.returncode) + ')')
    try:
        root = ET.fromstring(result.stdout)
        entries = list(root.iter('environmentVariable'))
        settings = {item.get('name'): item.get('value', '') for item in entries}
    except ET.ParseError:
        raise RuntimeError('Production web.config XML could not be parsed') from None
    duplicate_names = sorted(name for name, count in Counter(item.get('name') for item in entries).items() if count > 1)
    print('web.config duplicate variable names: ' + (', '.join(duplicate_names) or 'none'))
    modes = [item.get('value', '') for item in entries if item.get('name') == 'WebsiteCheckout__StripeMode']
    print('web.config Stripe mode entries: ' + ', '.join(mode if mode in {'live', 'Live'} else '<other>' for mode in modes))
    aspnet = root.find('.//aspNetCore')
    if (aspnet is None or aspnet.get('processPath') != 'dotnet'
            or aspnet.get('arguments') != r'.\AnisePOS.Api.dll'
            or aspnet.get('hostingModel') != 'inprocess'):
        raise RuntimeError('Production IIS entry point differs from the reviewed framework-dependent package')
    if settings.get('ASPNETCORE_ENVIRONMENT') != 'Production':
        raise RuntimeError('Production ASPNETCORE_ENVIRONMENT missing or incorrect')
    connection = settings.get('ConnectionStrings__AniseDatabase', '')
    server = re.search(r'(?:^|;)\s*(?:Server|Data Source)\s*=\s*([^;]+)', connection, re.I)
    database = re.search(r'(?:^|;)\s*(?:Database|Initial Catalog)\s*=\s*([^;]+)', connection, re.I)
    if (not server or server.group(1).strip().lower() != 'mssqluk20.prosql.net'
            or not database or database.group(1).strip().lower() != 'anisedatabase'
            or not re.search(r'(?:^|;)\s*(?:User Id|UID)\s*=', connection, re.I)
            or not re.search(r'(?:^|;)\s*(?:Password|PWD)\s*=', connection, re.I)):
        raise RuntimeError('Production database connection identity or authentication fields differ from approved values')
    print('PASS: production IIS entry point, environment and database target; credentials withheld')
    missing = sorted(STRIPE_SETTINGS - settings.keys())
    print('web.config Stripe setting names: ' + (', '.join(sorted(STRIPE_SETTINGS & settings.keys())) or 'none'))
    if missing:
        raise RuntimeError('Required Stripe setting names absent from web.config: ' + ', '.join(missing))
    if settings['WebsiteCheckout__StripeSessionsEnabled'].lower() != 'true' or settings['WebsiteCheckout__StripeMode'] != 'live':
        raise RuntimeError('Stripe Checkout enable flag or mode is not production-ready')
    if not settings['Stripe__SecretKey'].startswith('sk_live_') or not settings['WebsiteCheckout__StripeWebhookSecret'].startswith('whsec_'):
        raise RuntimeError('Stripe secret entries are missing or have the wrong key type')
    for name in ('WebsiteCheckout__StripeSuccessUrl', 'WebsiteCheckout__StripeCancelUrl'):
        url = urlparse(settings[name])
        if url.scheme != 'https' or url.hostname not in {'loveanise.co.uk', 'www.loveanise.co.uk'} or url.port is not None or url.username or url.password or url.fragment:
            raise RuntimeError(name + ' is not an approved production URL')
    if duplicate_names:
        raise RuntimeError('Production web.config contains duplicate environment variable names')
    print('PASS: web.config contains production Stripe Checkout settings with valid modes and URL hosts; values withheld')


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
    if MANIFEST not in site:
        raise RuntimeError('Production deployment manifest is missing from /wwwroot')
    curl_manifest(config)
    curl_web_config(config)
    if not configured:
        raise RuntimeError('PROD_FTPS_ROOT differs from the verified /wwwroot destination')
    print('PASS: curl verified the production POS /wwwroot destination; Python comparison complete')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        print('FAIL: ' + str(error))
        raise SystemExit(1)
