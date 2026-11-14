import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock
from zipfile import ZipFile

from deployment.v2 import engine, model, preflight, transport

TARGET = model.Target("staging", "wsuk32.pronameserver.com", "stageposftp", "/stageposftp/wwwroot",
                      "https://pos-staging.loveanise.co.uk", "anisedb")
PROD = model.Target("production", "wsuk32.pronameserver.com", "liveposftp", "/wwwroot",
                    "https://pos.loveanise.co.uk", "anisedatabase")
OLD = "a" * 40
NEW = "b" * 40


class FakeFTP:
    def __init__(self, target=TARGET):
        self.target = target
        self.path = target.root
        self.files = {"web.config": b"hosted config"}
        self.dirs = {"wwwroot"}
        self.fail_put = None

    def cwd(self, path):
        if path == self.target.root:
            self.path = path
        elif path in self.dirs:
            self.path = self.target.root + "/" + path
        else:
            raise ValueError("Unknown directory")

    def _name(self, name):
        prefix = self.path.removeprefix(self.target.root).strip("/")
        return (prefix + "/" if prefix else "") + name

    def list(self):
        prefix = self.path.removeprefix(self.target.root).strip("/")
        if prefix:
            prefix += "/"
        return [x[len(prefix):] for x in self.files if x.startswith(prefix) and "/" not in x[len(prefix):]] + [
            x for x in self.dirs if "/" not in x and not prefix]

    def get(self, name):
        return self.files[self._name(name)]

    def put(self, name, data):
        if self.fail_put == name:
            raise OSError("simulated upload failure")
        self.files[self._name(name)] = data

    def delete(self, name):
        del self.files[self._name(name)]

    def rename(self, source, destination):
        self.files[self._name(destination)] = self.files.pop(self._name(source))

    def mkdir(self, name):
        self.dirs.add(name)


def artifact(tmp, dll=b"old"):
    root = Path(tmp)
    (root / "wwwroot").mkdir(parents=True, exist_ok=True)
    (root / "AnisePOS.Api.dll").write_bytes(dll)
    (root / "wwwroot/pos.html").write_bytes(b"pos")
    rows = []
    for name in ("AnisePOS.Api.dll", "wwwroot/pos.html"):
        rows.append(hashlib.sha256((root / name).read_bytes()).hexdigest() + "  " + name)
    (root / "release-manifest.sha256").write_text("\n".join(rows) + "\n")
    return root


def baseline(ftp, root, release=OLD):
    files, digest = model.package(root)
    ftp.files.update({name: (root / name).read_bytes() for name in files})
    raw = model.state_bytes(ftp.target, release, digest, "healthy", files)
    for name in (engine.CURRENT, engine.HEALTHY, engine.record_name(release)):
        ftp.files[name] = raw


class V2Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = artifact(Path(self.temp.name) / "package")

    def test_package_rejects_config_and_unlisted_files(self):
        model.package(self.root)
        (self.root / "appsettings.Production.json").write_text("{}")
        with self.assertRaises(ValueError):
            model.package(self.root)
        (self.root / "appsettings.Production.json").unlink()
        (self.root / "release-manifest.sha256").write_text("0" * 64 + "  web.config\n")
        with self.assertRaises(ValueError):
            model.package(self.root)

    def test_package_rejects_unlisted_symlink(self):
        (self.root / "wwwroot/linked").symlink_to(self.root / "AnisePOS.Api.dll")
        with self.assertRaises(ValueError):
            model.package(self.root)

    def test_seed_verifies_live_bytes_and_health_before_writing_state(self):
        ftp = FakeFTP()
        with self.assertRaises(RuntimeError):
            engine.seed(ftp, TARGET, self.root, OLD, health_check=lambda _: None)
        self.assertNotIn(engine.CURRENT, ftp.files)
        for name in model.package(self.root)[0]:
            ftp.files[name] = (self.root / name).read_bytes()
        engine.seed(ftp, TARGET, self.root, OLD, health_check=lambda _: None)
        self.assertEqual(model.parse_state(ftp.files[engine.CURRENT], TARGET)["phase"], "healthy")

    def test_upload_failure_keeps_maintenance_and_healthy_pointer(self):
        ftp = FakeFTP()
        baseline(ftp, self.root)
        new = artifact(Path(self.temp.name) / "new", b"new")
        ftp.fail_put = "AnisePOS.Api.dll"
        with self.assertRaises(OSError):
            engine.deploy(ftp, TARGET, new, NEW, health_check=lambda _: None, sleeper=lambda _: None)
        self.assertIn(engine.OFFLINE, ftp.files)
        self.assertEqual(model.parse_state(ftp.files[engine.HEALTHY], TARGET)["release"], OLD)
        self.assertEqual(model.parse_state(ftp.files[engine.record_name(NEW)], TARGET)["phase"], "failed")

    def test_disposable_staging_ftps_probe_cleans_up(self):
        ftp = FakeFTP()
        result = engine.disposable_ftps_probe(ftp, TARGET)
        self.assertIn("verified", result)
        self.assertEqual(ftp.files, {"web.config": b"hosted config"})
        with self.assertRaises(ValueError):
            engine.disposable_ftps_probe(FakeFTP(PROD), PROD)

    def test_health_failure_is_not_recorded_as_success_and_rollback_recovers(self):
        ftp = FakeFTP()
        baseline(ftp, self.root)
        archive = Path(self.temp.name) / "backup.zip"
        engine.backup_current(ftp, TARGET, archive)
        new = artifact(Path(self.temp.name) / "new", b"new")
        with self.assertRaises(RuntimeError):
            engine.deploy(ftp, TARGET, new, NEW, health_check=lambda _: (_ for _ in ()).throw(RuntimeError("500.30")), sleeper=lambda _: None)
        self.assertIn(engine.OFFLINE, ftp.files)
        self.assertEqual(model.parse_state(ftp.files[engine.HEALTHY], TARGET)["release"], OLD)
        self.assertEqual(model.parse_state(ftp.files[engine.record_name(NEW)], TARGET)["history"],
                         ["uploaded", "installed", "failed"])
        self.assertEqual(engine.rollback(ftp, TARGET, archive, NEW, health_check=lambda _: None, sleeper=lambda _: None),
                         "verified healthy after rollback")
        self.assertNotIn(engine.OFFLINE, ftp.files)
        self.assertEqual(ftp.files["AnisePOS.Api.dll"], b"old")

    def test_backups_refuse_live_drift(self):
        ftp = FakeFTP()
        baseline(ftp, self.root)
        ftp.files["AnisePOS.Api.dll"] = b"changed outside deploy"
        with self.assertRaises(RuntimeError):
            engine.backup_current(ftp, TARGET, Path(self.temp.name) / "backup.zip")

    def test_rollback_removes_only_verified_failed_release_addition(self):
        ftp = FakeFTP()
        baseline(ftp, self.root)
        archive = Path(self.temp.name) / "backup.zip"
        engine.backup_current(ftp, TARGET, archive)
        new = artifact(Path(self.temp.name) / "new", b"new")
        (new / "wwwroot/extra.js").write_bytes(b"new asset")
        with (new / "release-manifest.sha256").open("a") as manifest:
            manifest.write(hashlib.sha256(b"new asset").hexdigest() + "  wwwroot/extra.js\n")
        with self.assertRaises(RuntimeError):
            engine.deploy(ftp, TARGET, new, NEW,
                          health_check=lambda _: (_ for _ in ()).throw(RuntimeError("500.30")), sleeper=lambda _: None)
        ftp.files["wwwroot/extra.js"] = b"unexpected edit"
        with self.assertRaises(RuntimeError):
            engine.rollback(ftp, TARGET, archive, NEW, health_check=lambda _: None, sleeper=lambda _: None)
        ftp.files["wwwroot/extra.js"] = b"new asset"
        engine.rollback(ftp, TARGET, archive, NEW, health_check=lambda _: None, sleeper=lambda _: None)
        self.assertNotIn("wwwroot/extra.js", ftp.files)
        self.assertEqual(ftp.files["web.config"], b"hosted config")

    def test_production_preflight_requires_independent_evidence_and_backups(self):
        digest = model.package(self.root)[1]
        evidence = dict(checked_at="2026-11-14T18:00:00Z", environment="production", site=PROD.url,
                        ftps_host=PROD.host, ftps_root=PROD.root, ftps_user=PROD.user,
                        sql_database=PROD.database, sql_server="mssqluk20.prosql.net",
                        artifact_sha256=digest, effective_iis_config_checked=True,
                        sql_readonly_probe_passed=True, schema_compatible=True,
                        runtime_compatible=True, startup_logging_verified=True,
                        pending_sql_migrations=[], backups={
                            "application": {"reference": "private/app/backup-123", "restorable": True, "retained": True},
                            "database": {"reference": "private/sql/backup-123", "restorable": True, "retained": True}})
        from datetime import datetime, timezone
        now = datetime(2026, 11, 14, 19, tzinfo=timezone.utc)
        probe = json.dumps({"database": "anisedatabase", "server": "mssqluk20.prosql.net", "connected": True,
                            "schema_compatible": True, "missing_columns": [], "missing_required_index": False})
        self.assertTrue(preflight.validate_evidence(json.dumps(evidence), PROD, digest, db_probe=probe, now=now))
        evidence["sql_database"] = "anisedb"
        with self.assertRaises(ValueError):
            preflight.validate_evidence(json.dumps(evidence), PROD, digest, db_probe=probe, now=now)

    def test_curl_transport_preserves_winserve_rename_and_secret_handling(self):
        ftp = transport.CurlFTPS(PROD, "private-password")
        with mock.patch.object(ftp, "_run") as run:
            ftp.rename("before", "after")
        run.assert_called_once_with("/wwwroot", listing=True, commands=("+RNFR before", "+RNTO after"))
        with mock.patch.object(transport.subprocess, "run") as run:
            run.return_value.returncode = 0
            run.return_value.stdout = b""
            ftp._run("/wwwroot", listing=True)
        args, kwargs = run.call_args
        self.assertIn("--ssl-reqd", args[0])
        self.assertIn("multicwd", args[0])
        self.assertNotIn("private-password", " ".join(args[0]))
        self.assertIn(b"private-password", kwargs["input"])


if __name__ == "__main__":
    unittest.main()
