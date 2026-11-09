import importlib.util
import os
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).parents[2]
SCRIPT = ROOT / 'scripts/production-deploy.py'
WORKFLOW = ROOT / '.github/workflows/production-release.yml'


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

    def test_configured_settings_pass_offline_preflight(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('no SQL or FTPS connection made', result.stdout)

    def test_staging_target_is_rejected(self):
        env = {
            'PROD_FTPS_EXPECTED_HOST': 'pos-staging.loveanise.co.uk',
            'PROD_FTPS_SECRET_HOST': 'pos-staging.loveanise.co.uk',
        }
        result = subprocess.run(
            [sys.executable, str(SCRIPT), '--mode', 'deploy', '--host', 'pos-staging.loveanise.co.uk',
             '--root', '/liveposftp/wwwroot', '--db-server', 'mssqluk20.prosql.net',
             '--db-name', 'anisedatabase'],
            cwd=ROOT,
            env={**os.environ, **{
                'PROD_FTPS_EXPECTED_ROOT': '/liveposftp/wwwroot',
                'PROD_FTPS_USERNAME': 'liveposftp',
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
        self.assertIn('Staging target refused', result.stderr)

    def test_workflow_uses_secrets_and_only_one_input(self):
        workflow = WORKFLOW.read_text()
        self.assertIn('PROD_DB_SERVER: ${{ secrets.PROD_DB_SERVER }}', workflow)
        self.assertIn('PROD_DB_NAME: ${{ secrets.PROD_DB_NAME }}', workflow)
        self.assertIn('PROD_BACKUP_CONFIRMED: ${{ secrets.PROD_BACKUP_CONFIRMED }}', workflow)
        self.assertNotIn('vars.PROD_DB_SERVER', workflow)
        self.assertNotIn('vars.PROD_DB_NAME', workflow)
        self.assertNotIn('vars.PROD_BACKUP_CONFIRMED', workflow)
        self.assertEqual(workflow.split('inputs:', 1)[1].count('staging_run_id:'), 1)


if __name__ == '__main__':
    unittest.main()
