import hashlib
import importlib.util
from pathlib import Path
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

    def mlsd(self):
        return [(name, {'type': 'file'}) for name in self.files]

    def cwd(self, path):
        pass

    def storbinary(self, command, stream):
        name = command[5:]
        if self.fail and name != 'app_offline.htm':
            raise OSError('simulated failure')
        self.files[name] = stream.read()

    def retrbinary(self, command, callback):
        callback(self.files[command[5:]])

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

    @patch.object(upload.time, 'sleep')
    def test_preserves_config_and_unrelated_files(self, _):
        ftp = FakeFTP(['web.config', 'appsettings.json', 'other.dll'])
        upload.upload(ftp, self.files + [('web.config', self.path, 'unused')])
        self.assertEqual(ftp.files['web.config'], b'original')
        self.assertEqual(ftp.files['appsettings.json'], b'original')
        self.assertEqual(ftp.files['other.dll'], b'original')
        self.assertEqual(ftp.deleted, ['app_offline.htm'])

    @patch.object(upload.time, 'sleep')
    def test_failure_leaves_site_offline(self, _):
        ftp = FakeFTP(fail=True)
        with self.assertRaises(OSError):
            upload.upload(ftp, self.files)
        self.assertIn('app_offline.htm', ftp.files)
        self.assertEqual(ftp.deleted, [])

    def test_existing_maintenance_marker_is_untouched(self):
        ftp = FakeFTP(['app_offline.htm'])
        with self.assertRaises(RuntimeError):
            upload.upload(ftp, self.files)
        self.assertEqual(ftp.files['app_offline.htm'], b'original')

    def test_rejects_manifest_path_escape(self):
        manifest = Path(self.temp.name) / 'manifest'
        manifest.write_text('abc  ../secret\n')
        with self.assertRaises(ValueError):
            upload.package_files(Path(self.temp.name), manifest)
