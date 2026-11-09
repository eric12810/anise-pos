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

APPLICATION = 'pos.loveanise.co.uk'
MANIFEST = '.anise-pos-production-deployment.json'
PENDING = MANIFEST + '.pending'
PREVIOUS = MANIFEST + '.previous'
MAINTENANCE = 'app_offline.htm'


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


def remote_files(ftp, root, hash_names):
    hashed = {}
    unhashed = []

    def walk(path):
        ftp.cwd(path)
        try:
            entries = list(ftp.mlsd())
        except ftplib.error_perm as error:
            if str(error)[:3] not in {'500', '502', '504'}:
                raise RuntimeError('Production directory listing failed') from error
            entries = []
            for listed in ftp.nlst():
                name = PurePosixPath(listed).name
                try:
                    ftp.cwd(name)
                except ftplib.error_perm:
                    try:
                        ftp.size(name)
                    except Exception as error:
                        raise RuntimeError('Could not distinguish an NLST entry as file or directory') from error
                    entries.append((name, {'type': 'file'}))
                else:
                    ftp.cwd(path)
                    entries.append((name, {'type': 'dir'}))
        for name, facts in entries:
            if name in {'.', '..'}:
                continue
            relative = PurePosixPath(path).joinpath(name).relative_to(root).as_posix()
            kind = facts.get('type', 'file')
            if kind == 'dir':
                walk(PurePosixPath(path).joinpath(name).as_posix())
                ftp.cwd(path)
            elif kind == 'file':
                if relative in hash_names:
                    digest = hashlib.sha256()
                    ftp.retrbinary('RETR ' + name, digest.update)
                    hashed[relative] = digest.hexdigest()
                else:
                    unhashed.append(relative)

    walk(root)
    return hashed, unhashed


def classify(name, package):
    lower = name.lower()
    if lower == 'web.config' or lower.startswith('appsettings'):
        return 'production configuration'
    if any(part in {'data', 'app_data', 'uploads', 'logs'} for part in PurePosixPath(name).parts):
        return 'runtime data or customer upload'
    if name in package:
        return 'managed application file'
    return 'unrelated or unexpected file'


def inventory(ftp, root, package):
    managed_package = {name for name in package if classify(name, package) == 'managed application file'}
    remote, unhashed = remote_files(ftp, root, managed_package | {'AnisePOS.Api.dll'})
    if 'AnisePOS.Api.dll' not in remote or 'web.config' not in remote:
        if 'web.config' not in unhashed:
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


@contextmanager
def connect():
    values = config()
    ftp = ftplib.FTP_TLS(context=ssl.create_default_context(), timeout=60)
    try:
        ftp.connect(values['LIVE_FTPS_HOST'], 21)
        ftp.auth()
        ftp.login(values['LIVE_FTPS_USERNAME'], values['LIVE_FTPS_PASSWORD'])
        ftp.prot_p()
        ftp.cwd(values['PROD_FTPS_ROOT'])
        yield ftp, values
    finally:
        try:
            ftp.quit()
        except Exception:
            ftp.close()


def inspect(args):
    package = package_names(Path(args.package))
    with connect() as ftp:
        report = inventory(ftp, os.environ['PROD_FTPS_ROOT'], package)
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
    for name in ('PROD_BASELINE_APP_BACKUP_REFERENCE', 'PROD_BASELINE_SQL_BACKUP_REFERENCE'):
        if not os.environ.get(name):
            raise SystemExit('Missing required backup reference: ' + name)
    if os.environ.get('PROD_BASELINE_OWNER_CONFIRMATION') != 'ESTABLISH PRODUCTION BASELINE':
        raise SystemExit('Owner confirmation is required to publish the production baseline')
    report = json.loads(Path(args.inventory).read_text())
    if report.get('application') != APPLICATION or not report.get('managed_remote_hashes'):
        raise SystemExit('Inspection report is missing verified production identity or managed hashes')
    with connect() as ftp:
        root = os.environ['PROD_FTPS_ROOT']
        first, unhashed = remote_files(ftp, root, set(json.loads(Path(args.inventory).read_text())['managed_remote_hashes']) | {'AnisePOS.Api.dll'})
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
                'application': os.environ['PROD_BASELINE_APP_BACKUP_REFERENCE'],
                'sql': os.environ['PROD_BASELINE_SQL_BACKUP_REFERENCE'],
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
