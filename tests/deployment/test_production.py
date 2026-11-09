import importlib.util
import os
import subprocess
import sys
import unittest
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
        self.assertIn('remote hashes changed since inspection', baseline)
        self.assertIn("if MANIFEST in names or PENDING in names", baseline)
        self.assertIn("ftp.rename(PENDING, MANIFEST)", baseline)
        self.assertIn("names_after", baseline)
        self.assertIn('hashlib.sha256()', baseline)


if __name__ == '__main__':
    unittest.main()
