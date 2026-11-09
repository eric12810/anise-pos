"""Guarded production release checks; deployment itself is delegated to the workflow uploader."""
import argparse
import os

def main():
    p = argparse.ArgumentParser(); p.add_argument('--mode', choices=('check','deploy'), required=True); p.add_argument('--host', required=True); p.add_argument('--root', required=True); p.add_argument('--db-server', required=True); p.add_argument('--db-name', required=True); p.add_argument('--backup-confirmation', default=''); p.add_argument('--db-confirmation', default=''); p.add_argument('--confirmation', default='')
    a=p.parse_args()
    settings = {
        'PROD_FTPS_EXPECTED_HOST': os.environ.get('PROD_FTPS_EXPECTED_HOST', ''),
        'PROD_FTPS_EXPECTED_ROOT': os.environ.get('PROD_FTPS_EXPECTED_ROOT', ''),
        'PROD_DB_SERVER': os.environ.get('PROD_DB_SERVER', ''),
        'PROD_DB_NAME': os.environ.get('PROD_DB_NAME', ''),
    }
    missing = [name for name, value in settings.items() if not value]
    if missing:
        raise SystemExit('Required production configuration is missing: ' + ', '.join(missing))
    expected = (settings['PROD_FTPS_EXPECTED_HOST'], settings['PROD_FTPS_EXPECTED_ROOT'], settings['PROD_DB_SERVER'], settings['PROD_DB_NAME'])
    if (a.host, a.root, a.db_server, a.db_name) != expected:
        raise SystemExit('Approved production FTPS/database identity does not match protected configuration')
    missing_ftps = [name for name, value in {
        'PROD_FTPS_SECRET_HOST': os.environ.get('PROD_FTPS_SECRET_HOST', ''),
        'PROD_FTPS_USERNAME': os.environ.get('PROD_FTPS_USERNAME', ''),
        'PROD_FTPS_PASSWORD': os.environ.get('PROD_FTPS_PASSWORD', ''),
    }.items() if not value]
    if missing_ftps:
        raise SystemExit('Required production FTPS configuration is missing: ' + ', '.join(missing_ftps))
    if os.environ['PROD_FTPS_SECRET_HOST'] != a.host:
        raise SystemExit('Approved production FTPS hostname does not match LIVE_FTPS_HOST')
    if a.host == os.environ.get('STAGING_FTPS_HOST') or 'staging' in a.root.lower() or a.db_name == 'anisedb': raise SystemExit('Staging target refused for production')
    if a.mode == 'deploy' and os.environ.get('PROD_BACKUP_CONFIRMED', '').lower() != 'true':
        raise SystemExit('Production backup confirmation is missing or invalid; protected PROD_BACKUP_CONFIRMED must equal true')
    print('CHECK ONLY: production target and database identity match configured approvals; no SQL or FTPS connection made.')
if __name__ == '__main__': main()
