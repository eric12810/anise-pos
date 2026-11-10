"""Inspect or explicitly bootstrap the production deployment baseline.

Inspection is read-only. Bootstrap publishes only the manifest after backup,
owner-confirmation, absence checks, and a second remote-hash inventory.
"""
import argparse
from contextlib import contextmanager
import ftplib
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import ssl
import time

APPLICATION = 'pos.loveanise.co.uk'
MANIFEST = '.anise-pos-production-deployment.json'
PENDING = MANIFEST + '.pending'
PREVIOUS = MANIFEST + '.previous'
MAINTENANCE = 'app_offline.htm'
FTP_TIMEOUT = 60
FTP_RETRIES = 3


def config():
    values = {name: os.environ.get(name, '') for name in (
        'LIVE_FTPS_HOST', 'LIVE_FTPS_USERNAME', 'LIVE_FTPS_PASSWORD', 'PROD_FTPS_ROOT')}
    missing = [name for name, value in values.items() if not value]
    if missing:
        raise SystemExit('Missing production FTPS configuration: ' + ', '.join(missing))
    if 'staging' in values['PROD_FTPS_ROOT'].lower():
        raise SystemExit('Production FTPS root is a staging directory')
    return values


def package_names(package):
    manifest = package / 'release-manifest.sha256'
    if not manifest.is_file():
        manifest = Path('artifacts/staging-manifest.sha256')
    rows = {}
    for line in manifest.read_text().splitlines():
        digest, name = line.split('  ', 1)
        rows[name] = digest
    return rows


def _operation_error(operation, error):
    return RuntimeError(f'Production FTPS read-only operation failed: {operation}: {error}')


def remote_files(ftp, root, hash_names, reconnect=None, log=print):
    root = PurePosixPath(root).as_posix().rstrip('/') or '/'
    hashed = {}
    unhashed = []
    checked = 0
    total = len(hash_names)
    current_dir = None
    replacement_ftps = []

    def transient(error):
        if isinstance(error, ftplib.error_perm):
            return str(error)[:3] in {'421', '425', '426', '450', '451', '452'}
        return isinstance(error, (OSError, EOFError, TimeoutError, ConnectionError))

    def retry(operation, action, restore_path=None):
        nonlocal ftp
        last_error = None
        for attempt in range(1, FTP_RETRIES + 1):
            try:
                return action()
            except (OSError, EOFError, TimeoutError, ftplib.Error) as error:
                last_error = error
                if attempt == FTP_RETRIES or reconnect is None or not transient(error):
                    raise _operation_error(operation, error) from error
                log(f'FTPS retry {attempt}/{FTP_RETRIES - 1}: {operation}')
                try:
                    ftp.close()
                except Exception:
                    pass
                time.sleep(min(attempt, 2))
                ftp = reconnect()
                replacement_ftps.append(ftp)
                if restore_path:
                    ftp.cwd(restore_path)
        raise _operation_error(operation, last_error)

    def cwd(path):
        nonlocal current_dir
        path = PurePosixPath(path).as_posix()
        retry(f'change directory to {path}', lambda: ftp.cwd(path), restore_path=current_dir)
        current_dir = path

    def hash_file(directory, name, relative, index):
        nonlocal checked
        log(f'Verifying managed file {index}/{total}: {relative}')
        def read():
            digest = hashlib.sha256()
            # A new digest is created for every attempt: partial transfers are discarded.
            # The default block size is compatible with Winserve's FTPS data channel.
            ftp.retrbinary('RETR ' + name, digest.update)
            return digest.hexdigest()
        result = retry(f'read-only hash of {relative}', read, restore_path=directory)
        checked += 1
        return result

    def walk(path):
        cwd(path)
        try:
            entries = list(ftp.mlsd())
        except ftplib.error_perm as error:
            if str(error)[:3] not in {'500', '502', '504'}:
                raise _operation_error(f'list directory {path}', error) from error
            entries = []
            for listed in retry(f'list directory {path} with NLST', ftp.nlst, restore_path=path):
                name = PurePosixPath(listed).name
                child = PurePosixPath(path).joinpath(name).as_posix()
                try:
                    ftp.cwd(child)
                except ftplib.error_perm as probe_error:
                    if str(probe_error)[:3] != '550':
                        raise _operation_error(f'probe directory {child}', probe_error) from probe_error
                    retry(f'check file entry {child}', lambda: ftp.size(child), restore_path=path)
                    entries.append((name, {'type': 'file'}))
                else:
                    cwd(path)
                    entries.append((name, {'type': 'dir'}))
        for name, facts in entries:
            if name in {'.', '..'}:
                continue
            relative = PurePosixPath(path).joinpath(name).relative_to(root).as_posix()
            kind = facts.get('type', 'file')
            if kind == 'dir':
                walk(PurePosixPath(path).joinpath(name).as_posix())
                cwd(path)
            elif kind == 'file':
                if relative in hash_names:
                    hashed[relative] = hash_file(path, name, relative, checked + 1)
                else:
                    unhashed.append(relative)

    try:
        walk(root)
        log(f'FTPS verification complete: {checked}/{total} managed files hashed')
        return hashed, unhashed
    finally:
        for replacement in replacement_ftps:
            try:
                replacement.quit()
            except Exception:
                try:
                    replacement.close()
                except Exception:
                    pass


def classify(name, package):
    lower = name.lower()
    if lower == 'web.config' or lower.startswith('appsettings'):
        return 'production configuration'
    if any(part in {'data', 'app_data', 'uploads', 'logs'} for part in PurePosixPath(name).parts):
        return 'runtime data or customer upload'
    if name in package:
        return 'managed application file'
    return 'unrelated or unexpected file'


def inventory(ftp, root, package, reconnect=None):
    managed_package = {name for name in package if classify(name, package) == 'managed application file'}
    remote, unhashed = remote_files(ftp, root, managed_package | {'AnisePOS.Api.dll'}, reconnect=reconnect)
    if 'AnisePOS.Api.dll' not in remote or 'web.config' not in unhashed:
        raise RuntimeError('Remote root does not contain the expected POS application identity files')
    managed = {name: remote[name] for name in package if name in remote}
    missing = sorted(name for name in managed_package if name not in remote)
    unexpected = {name: classify(name, package) for name in unhashed}
    return {
        'version': 1,
        'application': APPLICATION,
        'root': root,
        'package_files': sorted(package),
        'remote_files': remote,
        'unhashed_remote_files': sorted(unhashed),
        'managed_remote_hashes': managed,
        'missing_from_remote': missing,
        'unexpected_remote_files': unexpected,
    }


def _open_ftp(values):
    ftp = ftplib.FTP_TLS(context=ssl.create_default_context(), timeout=FTP_TIMEOUT)
    try:
        ftp.connect(values['LIVE_FTPS_HOST'], 21)
        ftp.auth()
        ftp.login(values['LIVE_FTPS_USERNAME'], values['LIVE_FTPS_PASSWORD'])
        ftp.prot_p()
        ftp.set_pasv(True)
        ftp.cwd(values['PROD_FTPS_ROOT'])
        return ftp
    except Exception:
        try:
            ftp.close()
        finally:
            raise


@contextmanager
def connect():
    values = config()
    ftp = _open_ftp(values)
    try:
        yield ftp
    finally:
        try:
            ftp.quit()
        except Exception:
            ftp.close()


def inspect(args):
    package = package_names(Path(args.package))
    with connect() as ftp:
        report = inventory(ftp, os.environ['PROD_FTPS_ROOT'], package,
                           reconnect=lambda: _open_ftp(config()))
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    (output / 'production-inventory.json').write_text(json.dumps(report, indent=2, sort_keys=True) + '\n')
    (output / 'production-conflicts.json').write_text(json.dumps({
        'missing_from_remote': report['missing_from_remote'],
        'unexpected_remote_files': report['unexpected_remote_files'],
    }, indent=2, sort_keys=True) + '\n')
    proposed = {
        'version': 1,
        'environment': 'production',
        'application': APPLICATION,
        'files': report['managed_remote_hashes'],
    }
    (output / 'proposed-production-baseline.json').write_text(json.dumps(proposed, indent=2, sort_keys=True) + '\n')
    (output / 'summary.txt').write_text(
        f"Remote files: {len(report['remote_files'])}\n"
        f"Managed files observed: {len(report['managed_remote_hashes'])}\n"
        f"Missing approved package files: {len(report['missing_from_remote'])}\n"
        f"Unexpected/configuration/runtime files: {len(report['unhashed_remote_files'])}\n"
        "No remote files were modified.\n"
    )


def bootstrap(args):
    if os.environ.get('PROD_BASELINE_BACKUP_VERIFIED', '').lower() != 'true':
        raise SystemExit('Verified application and SQL backup confirmation is required')
    backup_references = {}
    for name in ('PROD_BASELINE_APP_BACKUP_REFERENCE', 'PROD_BASELINE_SQL_BACKUP_REFERENCE'):
        if not os.environ.get(name):
            raise SystemExit('Missing required backup reference: ' + name)
        reference = os.environ[name].strip()
        if reference.lower() in {'true', 'false', 'verified', 'confirmed', 'yes'}:
            raise SystemExit(f'Backup reference {name} is only a confirmation flag, not an auditable backup identity')
        backup_references[name] = reference
    if backup_references['PROD_BASELINE_APP_BACKUP_REFERENCE'] == backup_references['PROD_BASELINE_SQL_BACKUP_REFERENCE']:
        raise SystemExit('Application and SQL backup references must identify distinct backups')
    if os.environ.get('PROD_BASELINE_OWNER_CONFIRMATION') != 'ESTABLISH PRODUCTION BASELINE':
        raise SystemExit('Owner confirmation is required to publish the production baseline')
    report = json.loads(Path(args.inventory).read_text())
    if report.get('application') != APPLICATION or not report.get('managed_remote_hashes'):
        raise SystemExit('Inspection report is missing verified production identity or managed hashes')
    with connect() as ftp:
        root = os.environ['PROD_FTPS_ROOT']
        first, unhashed = remote_files(
            ftp, root,
            set(json.loads(Path(args.inventory).read_text())['managed_remote_hashes']) | {'AnisePOS.Api.dll'},
            reconnect=lambda: _open_ftp(config()),
        )
        names = set(first) | set(unhashed)
        if MANIFEST in names or PENDING in names or PREVIOUS in names or MAINTENANCE in names:
            raise SystemExit('Production baseline publication refused: manifest or interrupted-deployment marker exists')
        expected = report['managed_remote_hashes']
        actual = {name: first.get(name) for name in expected}
        if actual != expected:
            raise SystemExit('Production baseline publication refused: remote hashes changed since inspection')
        payload = {
            'version': 1,
            'environment': 'production',
            'application': APPLICATION,
            'backup_references': {
                'application': backup_references['PROD_BASELINE_APP_BACKUP_REFERENCE'],
                'sql': backup_references['PROD_BASELINE_SQL_BACKUP_REFERENCE'],
            },
            'files': expected,
        }
        # Publish only a manifest; never upload application files or config.
        ftp.storbinary('STOR ' + PENDING, io.BytesIO(json.dumps(payload, separators=(',', ':'), sort_keys=True).encode()))
        names_after = set()
        try:
            names_after = {name for name, _ in ftp.mlsd()}
        except ftplib.error_perm:
            names_after = {PurePosixPath(name).name for name in ftp.nlst()}
        if MANIFEST in names_after:
            raise SystemExit('Baseline publication refused: production manifest appeared during publication')
        ftp.rename(PENDING, MANIFEST)
        print('Baseline manifest published; no application, configuration, runtime, or SQL files were changed')


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest='command', required=True)
    inspect_parser = sub.add_parser('inspect')
    inspect_parser.add_argument('--package', required=True)
    inspect_parser.add_argument('--output', required=True)
    bootstrap_parser = sub.add_parser('bootstrap')
    bootstrap_parser.add_argument('--inventory', required=True)
    args = parser.parse_args()
    if args.command == 'inspect':
        inspect(args)
    else:
        bootstrap(args)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Production baseline operation failed: ' + str(error))
        raise SystemExit(1)
