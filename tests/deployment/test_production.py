import importlib.util
import ftplib
from contextlib import contextmanager
import os
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).parents[2]
SCRIPT = ROOT / 'scripts/production-deploy.py'
UPLOADER = ROOT / 'scripts/upload-staging.py'
DIAGNOSTIC = ROOT / 'scripts/production-ftps-diagnostic.py'
BASELINE = ROOT / 'scripts/production-baseline.py'
WORKFLOW = ROOT / '.github/workflows/production-release.yml'


def production_uploader_module():
    spec = importlib.util.spec_from_file_location('production_uploader', UPLOADER)
    module = importlib.util.module_from_spec(spec)
    with mock.patch.dict(os.environ, {'DEPLOYMENT_ENV': 'production'}, clear=False):
        spec.loader.exec_module(module)
    return module


def baseline_module():
    spec = importlib.util.spec_from_file_location('production_baseline', BASELINE)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class NlstFTP:
    def __init__(self):
        self.path = '/root'
        self.retrieved = []
        self.directories = {'/root', '/root/bin', '/root/app_data'}
        self.files = {
            '/root/AnisePOS.Api.dll': b'api',
            '/root/web.config': b'secret-config',
            '/root/bin/dep.dll': b'dep',
            '/root/app_data/customer.db': b'sensitive-data',
        }

    def cwd(self, path):
        if path.startswith('/'):
            candidate = path
        else:
            candidate = self.path.rstrip('/') + '/' + path
        if candidate not in self.directories:
            raise ftplib.error_perm('550 not a directory')
        self.path = candidate

    def mlsd(self):
        raise ftplib.error_perm('500 MLSD unsupported')

    def nlst(self):
        prefix = self.path.rstrip('/') + '/'
        names = []
        for candidate in list(self.directories) + list(self.files):
            if candidate.startswith(prefix):
                remainder = candidate[len(prefix):]
                if '/' not in remainder:
                    names.append(remainder)
        return names

    def size(self, name):
        candidate = name if name.startswith('/') else self.path.rstrip('/') + '/' + name
        if candidate not in self.files:
            raise ftplib.error_perm('550 no size')
        return len(self.files[candidate])

    def retrbinary(self, command, callback):
        name = command.split(' ', 1)[1]
        candidate = self.path.rstrip('/') + '/' + name
        self.retrieved.append(candidate)
        callback(self.files[candidate])


class ManifestFTP:
    def __init__(self, payload=b''):
        self.payload = payload

    def retrbinary(self, command, callback):
        callback(self.payload)


class ProductionDeploymentTests(unittest.TestCase):
    def run_script(self, extra_env=None):
        env = {
            **os.environ,
            'PROD_FTPS_EXPECTED_HOST': 'pos.loveanise.co.uk',
            'PROD_FTPS_EXPECTED_ROOT': '/liveposftp/wwwroot',
            'PROD_FTPS_SECRET_HOST': 'pos.loveanise.co.uk',
            'PROD_FTPS_USERNAME': 'liveposftp',
            'PROD_FTPS_PASSWORD': 'not-printed',
            'PROD_DB_SERVER': 'mssqluk20.prosql.net',
            'PROD_DB_NAME': 'anisedatabase',
            'PROD_BACKUP_CONFIRMED': 'true',
            'STAGING_FTPS_HOST': 'pos-staging.loveanise.co.uk',
        }
        env.update(extra_env or {})
        return subprocess.run(
            [sys.executable, str(SCRIPT), '--mode', 'deploy', '--host', 'pos.loveanise.co.uk',
             '--root', '/liveposftp/wwwroot', '--db-server', 'mssqluk20.prosql.net',
             '--db-name', 'anisedatabase'],
            cwd=ROOT, env=env, capture_output=True, text=True, check=False,
        )

    def test_missing_sql_configuration_fails_with_setting_names_only(self):
        result = self.run_script({'PROD_DB_SERVER': '', 'PROD_DB_NAME': ''})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('PROD_DB_SERVER', result.stderr)
        self.assertIn('PROD_DB_NAME', result.stderr)
        self.assertNotIn('mssqluk20.prosql.net', result.stderr)
        self.assertNotIn('anisedatabase', result.stderr)

    def test_missing_backup_confirmation_fails(self):
        result = self.run_script({'PROD_BACKUP_CONFIRMED': ''})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('backup confirmation', result.stderr.lower())

    def test_missing_ftps_configuration_fails_with_setting_names_only(self):
        result = self.run_script({'PROD_FTPS_EXPECTED_HOST': '', 'PROD_FTPS_EXPECTED_ROOT': ''})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('PROD_FTPS_EXPECTED_HOST', result.stderr)
        self.assertIn('PROD_FTPS_EXPECTED_ROOT', result.stderr)
        self.assertNotIn('not-printed', result.stderr)

    def test_configured_settings_pass_offline_preflight(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('no SQL or FTPS connection made', result.stdout)

    def test_staging_target_is_rejected(self):
        env = {
            'PROD_FTPS_EXPECTED_HOST': 'pos-staging.loveanise.co.uk',
            'PROD_FTPS_SECRET_HOST': 'pos-staging.loveanise.co.uk',
            'PROD_FTPS_USERNAME': 'stageposftp',
            'STAGING_FTPS_USERNAME': 'stageposftp',
            'STAGING_FTPS_ROOT': '/stageposftp/wwwroot',
        }
        result = subprocess.run(
            [sys.executable, str(SCRIPT), '--mode', 'deploy', '--host', 'pos-staging.loveanise.co.uk',
             '--root', '/stageposftp/wwwroot', '--db-server', 'mssqluk20.prosql.net',
             '--db-name', 'anisedatabase'],
            cwd=ROOT,
            env={**os.environ, **{
                'PROD_FTPS_EXPECTED_ROOT': '/stageposftp/wwwroot',
                'PROD_FTPS_PASSWORD': 'not-printed',
                'PROD_DB_SERVER': 'mssqluk20.prosql.net',
                'PROD_DB_NAME': 'anisedatabase',
                'PROD_BACKUP_CONFIRMED': 'true',
                'STAGING_FTPS_HOST': 'pos-staging.loveanise.co.uk',
                **env,
            }},
            capture_output=True, text=True, check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('account and root match the staging destination', result.stderr)

    def test_shared_ftp_hostname_is_allowed_for_distinct_production_account_and_root(self):
        result = self.run_script({
            'STAGING_FTPS_HOST': 'pos.loveanise.co.uk',
            'STAGING_FTPS_USERNAME': 'stageposftp',
            'STAGING_FTPS_ROOT': '/stageposftp/wwwroot',
            'PROD_FTPS_EXPECTED_HOST': 'pos.loveanise.co.uk',
            'PROD_FTPS_SECRET_HOST': 'pos.loveanise.co.uk',
            'PROD_FTPS_USERNAME': 'liveposftp',
            'PROD_FTPS_EXPECTED_ROOT': '/liveposftp/wwwroot',
        })
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_workflow_uses_secrets_and_only_one_input(self):
        workflow = WORKFLOW.read_text()
        self.assertIn('PROD_DB_SERVER: ${{ secrets.PROD_DB_SERVER }}', workflow)
        self.assertIn('PROD_DB_NAME: ${{ secrets.PROD_DB_NAME }}', workflow)
        self.assertIn('PROD_BACKUP_CONFIRMED: ${{ secrets.PROD_BACKUP_CONFIRMED }}', workflow)
        self.assertNotIn('vars.PROD_DB_SERVER', workflow)
        self.assertNotIn('vars.PROD_DB_NAME', workflow)
        self.assertNotIn('vars.PROD_BACKUP_CONFIRMED', workflow)
        self.assertIn('PROD_FTPS_EXPECTED_HOST: ${{ secrets.LIVE_FTPS_HOST }}', workflow)
        self.assertIn('DEPLOY_FTPS_HOST: ${{ secrets.LIVE_FTPS_HOST }}', workflow)
        self.assertIn('DEPLOYMENT_ENV: production', workflow)
        self.assertNotIn("vars.PROD_FTPS_HOST ||", workflow)
        self.assertNotIn("vars.PROD_FTPS_ROOT ||", workflow)
        self.assertEqual(workflow.split('inputs:', 1)[1].count('staging_run_id:'), 1)
        diagnostic = (ROOT / '.github/workflows/production-ftps-diagnostic.yml').read_text()
        self.assertIn('production-ftps-diagnostic.py', diagnostic)
        self.assertIn('PROD_FTPS_ROOT: ${{ vars.PROD_FTPS_ROOT }}', diagnostic)

    def test_production_uploader_has_no_staging_fallback(self):
        uploader = UPLOADER.read_text()
        production_block = uploader.split("if DEPLOYMENT_ENV == 'production':", 1)[1].split("else:", 1)[0]
        self.assertNotIn('STAGING_FTPS_PASSWORD', production_block)
        self.assertNotIn('stageposftp', production_block)
        self.assertNotIn('/stageposftp/wwwroot', production_block)
        self.assertIn("DEPLOYED_MANIFEST = '.anise-pos-production-deployment.json'", production_block)

    def test_production_missing_manifest_fails_closed(self):
        module = production_uploader_module()
        with self.assertRaisesRegex(RuntimeError, 'No verified production deployment baseline manifest'):
            module.read_deployed_manifest(ManifestFTP(), set())

    def test_production_malformed_manifest_fails_closed(self):
        module = production_uploader_module()
        with self.assertRaisesRegex(RuntimeError, 'Invalid remote deployment manifest'):
            module.read_deployed_manifest(ManifestFTP(b'{"version": 1}'), {module.DEPLOYED_MANIFEST})

    def test_production_interrupted_deployment_fails_before_upload(self):
        module = production_uploader_module()
        ftp = mock.Mock()
        ftp.mlsd.return_value = [('app_offline.htm', {})]
        with self.assertRaisesRegex(RuntimeError, 'Maintenance marker already present'):
            module.upload(ftp, [])
        ftp.storbinary.assert_not_called()

    def test_diagnostic_missing_configuration_is_secret_safe(self):
        env = {key: '' for key in ('LIVE_FTPS_HOST', 'LIVE_FTPS_USERNAME', 'LIVE_FTPS_PASSWORD', 'PROD_FTPS_ROOT')}
        result = subprocess.run([sys.executable, str(DIAGNOSTIC)], cwd=ROOT,
                                env={**os.environ, **env}, capture_output=True, text=True, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('LIVE_FTPS_HOST', result.stdout)
        self.assertIn('LIVE_FTPS_PASSWORD', result.stdout)
        self.assertNotIn('not-printed', result.stdout)

    def test_baseline_bootstrap_requires_verified_backups_before_connecting(self):
        inventory = ROOT / 'tests/deployment/temporary-baseline-inventory.json'
        inventory.write_text('{"application":"pos.loveanise.co.uk","managed_remote_hashes":{"x":"y"}}\n')
        try:
            result = subprocess.run(
                [sys.executable, str(BASELINE), 'bootstrap', '--inventory', str(inventory)],
                cwd=ROOT,
                env={**os.environ, 'LIVE_FTPS_HOST': 'host', 'LIVE_FTPS_USERNAME': 'user',
                     'LIVE_FTPS_PASSWORD': 'not-printed', 'PROD_FTPS_ROOT': '/liveposftp/wwwroot',
                     'PROD_BASELINE_BACKUP_VERIFIED': 'false'},
                capture_output=True, text=True, check=False,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('backup confirmation', result.stderr.lower())
            self.assertNotIn('not-printed', result.stderr)
        finally:
            inventory.unlink(missing_ok=True)

    def test_baseline_procedure_rechecks_hashes_and_handles_races(self):
        baseline = BASELINE.read_text()
        self.assertIn('expected POS application identity files', baseline)
        self.assertIn('remote hashes changed since inspection', baseline)
        self.assertIn("if MANIFEST in names or PENDING in names", baseline)
        self.assertIn("ftp.rename(PENDING, MANIFEST)", baseline)
        self.assertIn("names_after", baseline)
        self.assertIn('hashlib.sha256()', baseline)

    def test_nlst_fallback_recurses_and_does_not_hash_sensitive_files(self):
        module = baseline_module()
        ftp = NlstFTP()
        hashed, unhashed = module.remote_files(ftp, '/root', {'AnisePOS.Api.dll', 'bin/dep.dll'})
        self.assertEqual(set(hashed), {'AnisePOS.Api.dll', 'bin/dep.dll'})
        self.assertIn('web.config', unhashed)
        self.assertIn('app_data/customer.db', unhashed)
        self.assertNotIn('/root/web.config', ftp.retrieved)
        self.assertNotIn('/root/app_data/customer.db', ftp.retrieved)
        self.assertIn('/root/bin/dep.dll', ftp.retrieved)

    def test_remote_hash_retries_interrupted_transfer_from_empty_digest(self):
        module = baseline_module()
        class SlowThenGood:
            def __init__(self, fail):
                self.fail = fail
                self.calls = 0
            def cwd(self, path): pass
            def mlsd(self): return [('managed.dll', {'type': 'file'})]
            def retrbinary(self, command, callback, blocksize=None):
                self.calls += 1
                callback(b'partial')
                if self.fail:
                    self.fail = False
                    raise TimeoutError('slow transfer')
                callback(b'complete')
            def close(self): pass
        first = SlowThenGood(True)
        second = SlowThenGood(False)
        logs = []
        hashed, _ = module.remote_files(first, '/root', {'managed.dll'}, reconnect=lambda: second, log=logs.append)
        self.assertEqual(hashed['managed.dll'], __import__('hashlib').sha256(b'partialcomplete').hexdigest())
        self.assertEqual(first.calls, 1)
        self.assertEqual(second.calls, 1)
        self.assertTrue(any('retry' in line for line in logs))

    def test_remote_hash_retry_exhaustion_identifies_exact_operation(self):
        module = baseline_module()
        class AlwaysTimeout:
            def cwd(self, path): pass
            def mlsd(self): return [('managed.dll', {'type': 'file'})]
            def retrbinary(self, command, callback, blocksize=None): raise TimeoutError('read operation timed out')
            def close(self): pass
        with self.assertRaisesRegex(RuntimeError, 'read-only hash of managed.dll'):
            module.remote_files(AlwaysTimeout(), '/root', {'managed.dll'}, reconnect=lambda: AlwaysTimeout(), log=lambda _: None)

    def test_nlst_nested_paths_remain_absolute_across_hash_reconnect(self):
        module = baseline_module()
        class NestedFTP:
            def __init__(self, fail_hash=False):
                self.path = '/root'
                self.fail_hash = fail_hash
                self.cwd_calls = []
            def cwd(self, path):
                if not path.startswith('/'):
                    raise ftplib.error_perm('550 relative path rejected')
                if path not in {'/root', '/root/runtimes', '/root/runtimes/linux'}:
                    raise ftplib.error_perm('550 missing directory')
                self.path = path
                self.cwd_calls.append(path)
            def mlsd(self):
                raise ftplib.error_perm('500 MLSD unsupported')
            def nlst(self):
                return {'/root': ['runtimes'], '/root/runtimes': ['linux'],
                        '/root/runtimes/linux': ['native.dll']}[self.path]
            def size(self, name):
                if name == '/root/runtimes/linux/native.dll': return 3
                raise ftplib.error_perm('550 missing file')
            def retrbinary(self, command, callback):
                self.cwd_calls.append(f'{self.path}:{command}')
                if self.fail_hash:
                    self.fail_hash = False
                    raise TimeoutError('interrupted transfer')
                callback(b'abc')
            def close(self): pass
        first = NestedFTP(True)
        replacement = NestedFTP(False)
        hashed, _ = module.remote_files(first, '/root', {'runtimes/linux/native.dll'},
                                         reconnect=lambda: replacement, log=lambda _: None)
        self.assertEqual(set(hashed), {'runtimes/linux/native.dll'})
        self.assertNotIn('/root/runtimes/runtimes', replacement.cwd_calls)
        self.assertIn('/root/runtimes/linux', replacement.cwd_calls)

    def test_bootstrap_rejects_confirmation_only_backup_reference(self):
        module = baseline_module()
        with self._temporary_directory() as temporary:
            inventory = Path(temporary) / 'inventory.json'
            inventory.write_text('{"application":"pos.loveanise.co.uk","managed_remote_hashes":{"x":"y"}}\n')
            env = {'PROD_BASELINE_BACKUP_VERIFIED': 'true',
                   'PROD_BASELINE_APP_BACKUP_REFERENCE': 'confirmed',
                   'PROD_BASELINE_SQL_BACKUP_REFERENCE': 'sql-ref'}
            with mock.patch.dict(os.environ, env, clear=False):
                with self.assertRaisesRegex(SystemExit, 'only a confirmation flag'):
                    module.bootstrap(SimpleNamespace(inventory=str(inventory)))

    def test_connection_context_closes_on_exception(self):
        module = baseline_module()
        ftp = mock.Mock()
        ftp.cwd.side_effect = RuntimeError('directory failure')
        ftp.quit.side_effect = RuntimeError('already closed')
        with mock.patch.dict(os.environ, {
            'LIVE_FTPS_HOST': 'host', 'LIVE_FTPS_USERNAME': 'user',
            'LIVE_FTPS_PASSWORD': 'not-printed', 'PROD_FTPS_ROOT': '/root'}), \
                mock.patch.object(module.ftplib, 'FTP_TLS', return_value=ftp):
            with self.assertRaises(RuntimeError):
                with module.connect():
                    pass
        ftp.close.assert_called()

    def test_connection_context_yields_ftp_object_and_allows_cwd(self):
        module = baseline_module()
        ftp = mock.Mock()
        with mock.patch.dict(os.environ, {
            'LIVE_FTPS_HOST': 'host', 'LIVE_FTPS_USERNAME': 'user',
            'LIVE_FTPS_PASSWORD': 'not-printed', 'PROD_FTPS_ROOT': '/root'}), \
                mock.patch.object(module.ftplib, 'FTP_TLS', return_value=ftp):
            with module.connect() as actual:
                actual.cwd('/root/again')
        self.assertIs(actual, ftp)
        ftp.cwd.assert_any_call('/root/again')
        ftp.quit.assert_called_once()

    def test_inspect_uses_mocked_ftp_context_and_writes_reports(self):
        module = baseline_module()
        with self.subTest('inspect'):
            with self._temporary_directory() as temporary:
                directory = Path(temporary)
                package = directory / 'package'
                output = directory / 'output'
                package.mkdir()
                (package / 'release-manifest.sha256').write_text('hash  AnisePOS.Api.dll\n')
                report = {
                    'remote_files': {'AnisePOS.Api.dll': 'remote-hash'},
                    'unhashed_remote_files': ['web.config'],
                    'managed_remote_hashes': {'AnisePOS.Api.dll': 'remote-hash'},
                    'missing_from_remote': [],
                    'unexpected_remote_files': {'web.config': 'production configuration'},
                }
                fake = mock.Mock()
                with mock.patch.object(module, 'connect', self._connection(fake)), \
                        mock.patch.object(module, 'inventory', return_value=report), \
                        mock.patch.dict(os.environ, {'PROD_FTPS_ROOT': '/liveposftp/wwwroot'}):
                    module.inspect(SimpleNamespace(package=str(package), output=str(output)))
                self.assertTrue((output / 'production-inventory.json').is_file())
                self.assertTrue((output / 'production-conflicts.json').is_file())
                self.assertTrue((output / 'proposed-production-baseline.json').is_file())

    def test_bootstrap_mocked_success_and_hash_mismatch(self):
        module = baseline_module()
        with self._temporary_directory() as temporary:
            directory = Path(temporary)
            inventory = directory / 'production-inventory.json'
            inventory.write_text('{"application":"pos.loveanise.co.uk","managed_remote_hashes":{"AnisePOS.Api.dll":"remote-hash"}}\n')
            fake = mock.Mock()
            fake.mlsd.return_value = []
            env = {
                'LIVE_FTPS_HOST': 'host', 'LIVE_FTPS_USERNAME': 'user', 'LIVE_FTPS_PASSWORD': 'not-printed',
                'PROD_FTPS_ROOT': '/root', 'PROD_BASELINE_BACKUP_VERIFIED': 'true',
                'PROD_BASELINE_APP_BACKUP_REFERENCE': 'app-ref', 'PROD_BASELINE_SQL_BACKUP_REFERENCE': 'sql-ref',
                'PROD_BASELINE_OWNER_CONFIRMATION': 'ESTABLISH PRODUCTION BASELINE',
            }
            with mock.patch.dict(os.environ, env), mock.patch.object(module, 'connect', self._connection(fake)), \
                    mock.patch.object(module, 'remote_files', return_value=({'AnisePOS.Api.dll': 'remote-hash'}, ['web.config'])):
                module.bootstrap(SimpleNamespace(inventory=str(inventory)))
            fake.rename.assert_called_once_with(module.PENDING, module.MANIFEST)
            fake.storbinary.assert_called_once()
            with mock.patch.dict(os.environ, env), mock.patch.object(module, 'connect', self._connection(fake)), \
                    mock.patch.object(module, 'remote_files', return_value=({'AnisePOS.Api.dll': 'changed-hash'}, ['web.config'])):
                with self.assertRaisesRegex(SystemExit, 'remote hashes changed'):
                    module.bootstrap(SimpleNamespace(inventory=str(inventory)))
            self.assertEqual(fake.storbinary.call_count, 1)

    @staticmethod
    def _connection(ftp):
        @contextmanager
        def context():
            yield ftp
        return context

    @staticmethod
    def _temporary_directory():
        return tempfile.TemporaryDirectory()


if __name__ == '__main__':
    unittest.main()
