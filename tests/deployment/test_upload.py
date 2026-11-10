import hashlib
import gzip
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('upload', 'scripts/upload-staging.py')
upload = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upload)


class FakeFTP:
    def __init__(self, existing=(), fail=False):
        self.files = {name: b'original' for name in existing}
        self.fail = fail
        self.deleted = []
        self.directories = []
        self.uploaded = []

    def mlsd(self):
        return [(name, {'type': 'file'}) for name in self.files]

    def cwd(self, path):
        self.directories.append(path)
        if path.startswith('/') and path != upload.REMOTE_ROOT:
            raise AssertionError('Operation outside staging wwwroot')

    def storbinary(self, command, stream):
        name = command[5:]
        if self.fail and name != 'app_offline.htm':
            raise OSError('simulated failure')
        self.files[name] = stream.read()
        self.uploaded.append(name)

    def retrbinary(self, command, callback):
        callback(self.files[command[5:]])

    def rename(self, old, new):
        if old not in self.files:
            raise upload.ftplib.error_perm('550 missing')
        if new in self.files:
            raise upload.ftplib.error_perm('550 exists')
        self.files[new] = self.files.pop(old)

    def delete(self, name):
        self.deleted.append(name)
        del self.files[name]


class UploadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'AnisePOS.Api.dll'
        self.path.write_bytes(b'package')
        self.files = [('AnisePOS.Api.dll', self.path, hashlib.sha256(b'package').hexdigest())]

    def manifest(self, files=None):
        return json.dumps({'version': 1, 'files': files or {
            'AnisePOS.Api.dll': self.files[0][2]}}).encode()

    @patch.object(upload.time, 'sleep')
    def test_first_upload_preserves_config_and_unrelated_files(self, _):
        ftp = FakeFTP(['web.config', 'appsettings.json', 'other.dll'])
        upload.upload(ftp, self.files + [('web.config', self.path, 'unused')])
        self.assertEqual(ftp.files['web.config'], b'original')
        self.assertEqual(ftp.files['appsettings.json'], b'original')
        self.assertEqual(ftp.files['other.dll'], b'original')
        self.assertIn(upload.DEPLOYED_MANIFEST, ftp.files)
        self.assertEqual(ftp.deleted, ['app_offline.htm'])
        self.assertIn('AnisePOS.Api.dll', ftp.uploaded)

    @patch.object(upload.time, 'sleep')
    def test_unchanged_package_is_no_op(self, sleep):
        ftp = FakeFTP()
        ftp.files[upload.DEPLOYED_MANIFEST] = self.manifest()
        upload.upload(ftp, self.files)
        self.assertEqual(ftp.uploaded, [])
        self.assertEqual(ftp.deleted, [])
        sleep.assert_not_called()

    @patch.object(upload.time, 'sleep')
    def test_changed_dll_is_uploaded_and_verified(self, _):
        ftp = FakeFTP()
        ftp.files[upload.DEPLOYED_MANIFEST] = self.manifest({'AnisePOS.Api.dll': 'a' * 64})
        upload.upload(ftp, self.files)
        self.assertEqual(ftp.files['AnisePOS.Api.dll'], b'package')
        self.assertIn(upload.DEPLOYED_MANIFEST, ftp.files)
        self.assertEqual(ftp.deleted, [upload.BACKUP_MANIFEST, 'app_offline.htm'])

    @patch.object(upload.time, 'sleep')
    def test_failure_leaves_site_offline(self, _):
        ftp = FakeFTP(fail=True)
        with self.assertRaises(OSError):
            upload.upload(ftp, self.files)
        self.assertIn('app_offline.htm', ftp.files)
        self.assertEqual(ftp.deleted, [])

    def test_existing_maintenance_marker_requires_recovery(self):
        ftp = FakeFTP(['app_offline.htm'])
        ftp.files['app_offline.htm'] = upload.MAINTENANCE
        with self.assertRaises(RuntimeError):
            upload.upload(ftp, self.files)
        self.assertEqual(ftp.files['app_offline.htm'], upload.MAINTENANCE)

    def test_invalid_remote_manifest_fails_closed(self):
        ftp = FakeFTP()
        ftp.files[upload.DEPLOYED_MANIFEST] = b'not JSON'
        with self.assertRaises(RuntimeError):
            upload.upload(ftp, self.files)
        self.assertNotIn('app_offline.htm', ftp.files)

    def test_manifest_pending_or_backup_requires_recovery(self):
        for name in (upload.PENDING_MANIFEST, upload.BACKUP_MANIFEST):
            ftp = FakeFTP([name])
            with self.subTest(name=name), self.assertRaises(RuntimeError):
                upload.upload(ftp, self.files)
            self.assertNotIn('app_offline.htm', ftp.files)

    def test_refuses_package_removal(self):
        ftp = FakeFTP()
        ftp.files[upload.DEPLOYED_MANIFEST] = self.manifest({
            'AnisePOS.Api.dll': self.files[0][2],
            'missing-old.dll': 'b' * 64,
        })
        with self.assertRaises(RuntimeError):
            upload.upload(ftp, self.files)
        self.assertNotIn('app_offline.htm', ftp.files)

    def test_rejects_manifest_path_escape(self):
        manifest = Path(self.temp.name) / 'manifest'
        manifest.write_text('abc  ../secret\n')
        with self.assertRaises(ValueError):
            upload.package_files(Path(self.temp.name), manifest)


class DirectoryTests(unittest.TestCase):
    def test_unsupported_mlsd_falls_back(self):
        from unittest.mock import Mock
        ftp = Mock()
        ftp.mlsd.side_effect = upload.ftplib.error_perm('500 unsupported')
        ftp.nlst.return_value = ['/web.config', 'appsettings.json']
        self.assertEqual(upload.directory_names(ftp), ['web.config', 'appsettings.json'])

    def test_permission_error_does_not_fall_back(self):
        from unittest.mock import Mock
        ftp = Mock()
        ftp.mlsd.side_effect = upload.ftplib.error_perm('550 denied')
        with self.assertRaises(upload.ftplib.error_perm):
            upload.directory_names(ftp)
        ftp.nlst.assert_not_called()

    def test_failed_fallback_is_not_empty_directory(self):
        from unittest.mock import Mock
        ftp = Mock()
        ftp.mlsd.side_effect = upload.ftplib.error_perm('500 unsupported')
        ftp.nlst.side_effect = upload.ftplib.error_perm('550 denied')
        with self.assertRaises(upload.ftplib.error_perm):
            upload.directory_names(ftp)


class PosAssetTests(unittest.TestCase):
    def test_precompressed_pos_page_matches_hybrid_source(self):
        root = Path('src/AnisePOS.Api/wwwroot')
        source = (root / 'pos.html').read_bytes()
        source_text = source.decode('utf-8')
        for marker in ('attachDishPress', 'cartLineKey', 'pointerdown', 'requiresSelection'):
            self.assertIn(marker, source_text)
        self.assertNotIn('onclick=()=>openProduct(cat.products', source_text)

        with gzip.open(root / 'pos.html.gz', 'rb') as compressed:
            self.assertEqual(compressed.read(), source)

        try:
            brotli = subprocess.run(
                ['brotli', '--decompress', '--stdout', str(root / 'pos.html.br')],
                check=True, capture_output=True,
            ).stdout
        except (FileNotFoundError, subprocess.CalledProcessError):
            self.skipTest('brotli CLI is not installed')
        self.assertEqual(brotli, source)
