import hashlib
import base64
import io
import json
import os
from pathlib import Path
import re
import tempfile
import zlib
import unittest
from unittest import mock
from zipfile import ZipFile

from deployment.v2 import cli, engine, model, preflight, transport, manual_sql, load_manual_snapshot, schema_guard, resolve

TARGET = model.Target("staging", "wsuk32.pronameserver.com", "stageposftp", "/stageposftp/wwwroot",
                      "https://pos-staging.loveanise.co.uk", "anisedb")
PROD = model.Target("production", "wsuk32.pronameserver.com", "liveposftp", "/wwwroot",
                    "https://pos.loveanise.co.uk", "anisedatabase")
OLD = "a" * 40
NEW = "b" * 40


class CliSeedGuards(unittest.TestCase):
    def test_retired_bypass_environment_does_not_skip_evidence(self):
        with mock.patch.dict(os.environ, {"V2_ONE_TIME_BYPASS_REQUESTED": "true"}), \
             mock.patch.object(cli, "target_from_env", return_value=PROD), \
             mock.patch.object(cli, "package", return_value=({}, "a" * 64)), \
             mock.patch.object(cli, "CurlFTPS") as ftps:
            with self.assertRaisesRegex(ValueError, "evidence is required"):
                cli.main(["preflight", "--environment", "production", "--release", OLD])
            ftps.assert_not_called()

    def run_seed(self, environment, *, activated="", approval=""):
        target = TARGET if environment == "staging" else PROD
        settings = {"V2_ACTIVATED": activated, "V2_SEED_APPROVAL": approval}
        with mock.patch.dict(os.environ, settings), \
             mock.patch.object(cli, "target_from_env", return_value=target), \
             mock.patch.object(cli, "package", return_value=({}, "digest")), \
             mock.patch.object(cli, "CurlFTPS") as ftps, \
             mock.patch.object(cli, "seed") as seed, \
             mock.patch.object(cli, "validate_evidence"), \
             mock.patch.object(cli, "read_private", side_effect=[b'{"sql_mode":"manual"}', b'{}', b'{}']):
            args = ["seed", "--environment", environment, "--release", OLD]
            if environment == "production":
                args += ["--evidence", __file__, "--sql-mode", "manual"]
            try:
                cli.main(args)
                error = None
            except ValueError as exc:
                error = exc
            return error, ftps.called, seed.called

    def test_unapproved_staging_seed_is_rejected_before_ftps(self):
        error, ftps, seed = self.run_seed("staging")
        self.assertIsNotNone(error)
        self.assertFalse(ftps)
        self.assertFalse(seed)

    def test_approved_staging_seed_allowed_before_activation(self):
        error, ftps, seed = self.run_seed("staging", approval="SEED POS V2 BASELINE")
        self.assertIsNone(error)
        self.assertTrue(ftps)
        self.assertTrue(seed)

    def test_production_seed_still_requires_activation_and_approval(self):
        self.assertFalse(self.run_seed("production", approval="SEED POS V2 BASELINE")[2])
        self.assertFalse(self.run_seed("production", activated="yes")[2])
        self.assertTrue(self.run_seed("production", activated="yes", approval="SEED POS V2 BASELINE")[2])

    def test_deploy_still_requires_activation(self):
        with mock.patch.dict(os.environ, {"V2_ACTIVATED": ""}), \
             mock.patch.object(cli, "target_from_env", return_value=TARGET), \
             mock.patch.object(cli, "package", return_value=({}, "digest")), \
             mock.patch.object(cli, "CurlFTPS") as ftps:
            with self.assertRaisesRegex(ValueError, "dormant"):
                cli.main(["deploy", "--environment", "staging", "--release", OLD])
            ftps.assert_not_called()

    def test_production_rollback_still_requires_activation(self):
        with mock.patch.dict(os.environ, {"V2_ACTIVATED": "", "V2_ROLLBACK_APPROVAL": "ROLLBACK POS PRODUCTION"}), \
             mock.patch.object(cli, "target_from_env", return_value=PROD), \
             mock.patch.object(cli, "CurlFTPS") as ftps:
            with self.assertRaisesRegex(ValueError, "rollback approval"):
                cli.main(["rollback", "--environment", "production", "--release", OLD, "--archive", "backup.zip"])
            ftps.assert_not_called()

    def test_first_release_requires_activation_and_exact_approval_before_ftps(self):
        args = ["initial-backup", "--environment", "production", "--release", OLD,
                "--archive", "legacy.zip", "--evidence", __file__, "--sql-mode", "manual"]
        with mock.patch.object(cli, "target_from_env", return_value=PROD), \
             mock.patch.object(cli, "package", return_value=({}, "a" * 64)), \
             mock.patch.object(cli, "read_private", side_effect=[b'{"sql_mode":"manual"}', b'{}', b'{}']), \
             mock.patch.object(cli, "validate_evidence", return_value=True), \
             mock.patch.object(cli, "CurlFTPS") as ftps:
            with mock.patch.dict(os.environ, {"V2_ACTIVATED": "", "V2_INITIAL_RELEASE_APPROVAL":
                                           "APPROVE FIRST POS V2 PRODUCTION RELEASE"}):
                with self.assertRaisesRegex(ValueError, "dormant"):
                    cli.main(args)
            ftps.assert_not_called()
        with mock.patch.object(cli, "target_from_env", return_value=PROD), \
             mock.patch.object(cli, "package", return_value=({}, "a" * 64)), \
             mock.patch.object(cli, "read_private", side_effect=[b'{"sql_mode":"manual"}', b'{}', b'{}']), \
             mock.patch.object(cli, "validate_evidence", return_value=True), \
             mock.patch.object(cli, "CurlFTPS") as ftps:
            with mock.patch.dict(os.environ, {"V2_ACTIVATED": "yes", "V2_INITIAL_RELEASE_APPROVAL": ""}):
                with self.assertRaisesRegex(ValueError, "approval"):
                    cli.main(args)
            ftps.assert_not_called()

    def test_production_preflight_only_never_constructs_transport(self):
        with tempfile.TemporaryDirectory() as folder:
            evidence = Path(folder) / "preflight.json"
            evidence.write_text('{"sql_mode":"manual"}')
            evidence.chmod(0o600)
            (Path(folder) / "sql-snapshot.json").write_text("{}")
            (Path(folder) / "sql-snapshot.json").chmod(0o600)
            (Path(folder) / "expected-schema.json").write_text("{}")
            (Path(folder) / "expected-schema.json").chmod(0o600)
            with mock.patch.object(cli, "target_from_env", return_value=PROD), \
                 mock.patch.object(cli, "package", return_value=({}, "a" * 64)), \
                 mock.patch.object(cli, "validate_evidence", return_value=True), \
                 mock.patch.object(cli, "CurlFTPS") as ftps, \
                 mock.patch.object(cli, "backup_current") as backup, \
                 mock.patch.object(cli, "deploy") as deploy, \
                 mock.patch.object(cli, "rollback") as rollback:
                cli.main(["preflight", "--environment", "production", "--evidence", str(evidence),
                          "--sql-snapshot", str(Path(folder) / "sql-snapshot.json"),
                          "--expected-schema", str(Path(folder) / "expected-schema.json"),
                          "--sql-mode", "manual", "--release", NEW])
                ftps.assert_not_called()
                backup.assert_not_called()
                deploy.assert_not_called()
                rollback.assert_not_called()
            evidence.chmod(0o644)
            with self.assertRaisesRegex(ValueError, "private"):
                cli.read_private(evidence)
            evidence.chmod(0o600)
            symlink = Path(folder) / "linked-evidence.json"
            symlink.symlink_to(evidence)
            with self.assertRaises(OSError):
                cli.read_private(symlink)


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
        elif path in self.dirs or path.removeprefix(self.target.root + "/") in self.dirs:
            self.path = self.target.root + "/" + path.removeprefix(self.target.root + "/")
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

    def timing_summary(self):
        return "simulated FTPS timing"


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

    def test_seed_rejects_other_pending_release_before_writing(self):
        ftp = FakeFTP()
        for name in model.package(self.root)[0]:
            ftp.files[name] = (self.root / name).read_bytes()
        ftp.files[engine.record_name(NEW) + ".pending"] = b"interrupted"
        with self.assertRaisesRegex(RuntimeError, "clean, unseeded"):
            engine.seed(ftp, TARGET, self.root, OLD, health_check=lambda _: None)
        self.assertNotIn(engine.CURRENT, ftp.files)

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

    def test_deploy_skips_only_preverified_unchanged_files(self):
        ftp = FakeFTP()
        baseline(ftp, self.root)
        replacement = artifact(Path(self.temp.name) / "replacement", b"new")
        with mock.patch.object(ftp, "put", wraps=ftp.put) as put:
            engine.deploy(ftp, TARGET, replacement, NEW, health_check=lambda _: None, sleeper=lambda _: None)
        uploaded = [call.args[0] for call in put.call_args_list]
        self.assertIn("AnisePOS.Api.dll", uploaded)
        self.assertNotIn("pos.html", uploaded)
        self.assertEqual(ftp.files["wwwroot/pos.html"], b"pos")

    def test_deploy_rejects_drift_before_skipping_unchanged_file(self):
        ftp = FakeFTP()
        baseline(ftp, self.root)
        ftp.files["wwwroot/pos.html"] = b"unexpected change"
        replacement = artifact(Path(self.temp.name) / "replacement", b"new")
        with mock.patch.object(ftp, "put", wraps=ftp.put) as put:
            with self.assertRaisesRegex(RuntimeError, "changed since healthy baseline"):
                engine.deploy(ftp, TARGET, replacement, NEW, health_check=lambda _: None, sleeper=lambda _: None)
        put.assert_not_called()

    def test_verified_backup_reuses_download_and_rejects_pointer_change(self):
        ftp = FakeFTP()
        baseline(ftp, self.root)
        archive = Path(self.temp.name) / "before.zip"
        verified = engine.backup_current(ftp, TARGET, archive)
        replacement = artifact(Path(self.temp.name) / "replacement", b"new")
        with mock.patch.object(ftp, "get", wraps=ftp.get) as get:
            engine.deploy(ftp, TARGET, replacement, NEW, verified_backup=verified,
                          health_check=lambda _: None, sleeper=lambda _: None)
        self.assertNotIn("wwwroot/pos.html", [call.args[0] for call in get.call_args_list])

        other = FakeFTP()
        baseline(other, self.root)
        altered = dict(verified, previous_healthy=NEW)
        other.files[engine.CURRENT] = json.dumps(altered).encode()
        with self.assertRaises(RuntimeError):
            engine.deploy(other, TARGET, replacement, NEW, verified_backup=verified,
                          health_check=lambda _: None, sleeper=lambda _: None)
        self.assertNotIn(engine.OFFLINE, other.files)

    def test_cli_rejects_changed_archive_before_ftps_deploy(self):
        ftp = FakeFTP(PROD)
        baseline(ftp, self.root)
        archive = Path(self.temp.name) / "before.zip"
        receipt = Path(self.temp.name) / "receipt.json"
        with mock.patch.dict(os.environ, {"V2_ACTIVATED": "yes"}), \
             mock.patch.object(cli, "target_from_env", return_value=PROD), \
             mock.patch.object(cli, "CurlFTPS", return_value=ftp), \
             mock.patch.object(cli, "validate_evidence"), \
             mock.patch.object(cli, "read_private", return_value=b'{"sql_mode":"manual"}'):
            cli.main(["backup", "--environment", "production", "--artifact", str(self.root),
                      "--evidence", __file__, "--sql-mode", "manual", "--release", NEW,
                      "--archive", str(archive), "--backup-receipt", str(receipt)])
            archive.write_bytes(archive.read_bytes() + b"changed")
            with mock.patch.object(cli, "deploy") as deploy:
                with self.assertRaisesRegex(ValueError, "archive changed"):
                    cli.main(["deploy", "--environment", "production", "--artifact", str(self.root),
                              "--evidence", __file__, "--sql-mode", "manual", "--release", NEW,
                              "--archive", str(archive), "--backup-receipt", str(receipt)])
                deploy.assert_not_called()

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
        verified = engine.backup_current(ftp, TARGET, archive)
        new = artifact(Path(self.temp.name) / "new", b"new")
        with self.assertRaises(RuntimeError):
            engine.deploy(ftp, TARGET, new, NEW, verified_backup=verified,
                          health_check=lambda _: (_ for _ in ()).throw(RuntimeError("500.30")), sleeper=lambda _: None)
        self.assertIn(engine.OFFLINE, ftp.files)
        self.assertEqual(model.parse_state(ftp.files[engine.HEALTHY], TARGET)["release"], OLD)
        self.assertEqual(model.parse_state(ftp.files[engine.record_name(NEW)], TARGET)["history"],
                         ["uploaded", "installed", "failed"])
        self.assertEqual(engine.rollback(ftp, TARGET, archive, NEW, health_check=lambda _: None, sleeper=lambda _: None),
                         "verified healthy after rollback")
        self.assertNotIn(engine.OFFLINE, ftp.files)
        self.assertEqual(ftp.files["AnisePOS.Api.dll"], b"old")

    def test_first_release_archives_unhealthy_legacy_bytes_then_establishes_health(self):
        ftp = FakeFTP(PROD)
        ftp.files["AnisePOS.Api.dll"] = b"old"
        ftp.files["wwwroot/pos.html"] = b"pos"
        legacy = {"version": 1, "environment": "production", "application": "pos.loveanise.co.uk",
                  "files": {name: hashlib.sha256(ftp.files[name]).hexdigest()
                            for name in ("AnisePOS.Api.dll", "wwwroot/pos.html")}}
        ftp.files[engine.LEGACY] = json.dumps(legacy).encode()
        archive = Path(self.temp.name) / "legacy.zip"
        self.assertEqual(len(engine.backup_legacy(ftp, PROD, archive)), 2)
        self.assertNotIn(engine.CURRENT, ftp.files)
        replacement = artifact(Path(self.temp.name) / "replacement", b"new")
        self.assertEqual(engine.deploy_initial(ftp, PROD, replacement, NEW, archive,
                                               health_check=lambda _: None, sleeper=lambda _: None),
                         "first V2 production release verified healthy")
        self.assertEqual(model.parse_state(ftp.files[engine.CURRENT], PROD)["phase"], "healthy")
        self.assertEqual(ftp.files["web.config"], b"hosted config")
        self.assertNotIn(engine.OFFLINE, ftp.files)

    def test_first_release_rejects_drift_and_retains_maintenance_on_health_failure(self):
        ftp = FakeFTP(PROD)
        ftp.files["AnisePOS.Api.dll"] = b"old"
        ftp.files["wwwroot/pos.html"] = b"pos"
        legacy = {"version": 1, "environment": "production", "application": "pos.loveanise.co.uk",
                  "files": {name: hashlib.sha256(ftp.files[name]).hexdigest()
                            for name in ("AnisePOS.Api.dll", "wwwroot/pos.html")}}
        ftp.files[engine.LEGACY] = json.dumps(legacy).encode()
        archive = Path(self.temp.name) / "legacy.zip"
        engine.backup_legacy(ftp, PROD, archive)
        replacement = artifact(Path(self.temp.name) / "replacement", b"new")
        ftp.files["AnisePOS.Api.dll"] = b"drift"
        with self.assertRaisesRegex(RuntimeError, "changed after backup"):
            engine.deploy_initial(ftp, PROD, replacement, NEW, archive,
                                  health_check=lambda _: None, sleeper=lambda _: None)
        self.assertNotIn(engine.OFFLINE, ftp.files)
        ftp.files["AnisePOS.Api.dll"] = b"old"
        with self.assertRaisesRegex(RuntimeError, "500.30"):
            engine.deploy_initial(ftp, PROD, replacement, NEW, archive,
                                  health_check=lambda _: (_ for _ in ()).throw(RuntimeError("500.30")),
                                  sleeper=lambda _: None)
        self.assertIn(engine.OFFLINE, ftp.files)
        self.assertNotIn(engine.HEALTHY, ftp.files)
        self.assertEqual(model.parse_state(ftp.files[engine.record_name(NEW)], PROD)["phase"], "failed")

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
                        schema_compatible=True,
                        runtime_compatible=True, startup_logging="verified", startup_logging_verified=True,
                        pending_sql_migrations=[], backups={
                            "application": {"reference": "private/app/backup-123", "restorable": True, "retained": True},
                            "database": {"reference": "private/sql/backup-123", "restorable": True, "retained": True}},
                        sql_mode="automated")
        from datetime import datetime, timezone
        now = datetime(2026, 11, 14, 19, tzinfo=timezone.utc)
        probe = json.dumps(dict(server="mssqluk20.prosql.net", database="anisedatabase", connected=True,
                                permission_restricted=True, schema_compatible=True, missing_columns=[],
                                missing_required_index=False))
        self.assertTrue(preflight.validate_evidence(json.dumps(evidence), PROD, digest, now=now, db_probe=probe))
        evidence["sql_database"] = "anisedb"
        with self.assertRaises(ValueError):
            preflight.validate_evidence(json.dumps(evidence), PROD, digest, now=now, db_probe=probe)
        evidence["sql_database"] = "anisedatabase"
        with self.assertRaises(ValueError):
            preflight.validate_evidence(json.dumps(evidence), PROD, digest, now=now)
        probe_bad = json.loads(probe)
        probe_bad["permission_restricted"] = False
        with self.assertRaises(ValueError):
            preflight.validate_evidence(json.dumps(evidence), PROD, digest, now=now, db_probe=json.dumps(probe_bad))
        evidence["sql_mode"] = "manual"
        evidence["manual_sql_review"] = {"observed_server": "mssqluk20.prosql.net"}
        with mock.patch.object(preflight.manual_sql, "validate", return_value=True):
            hosted = evidence | {"startup_logging": "host_unavailable", "startup_logging_verified": False,
                                 "startup_logging_limitation_acknowledged": True}
            self.assertTrue(preflight.validate_evidence(json.dumps(hosted), PROD, digest, now=now,
                                                        release=NEW, sql_snapshot=b"{}", expected_schema=b"{}"))
            for field in ("startup_logging", "startup_logging_limitation_acknowledged"):
                changed = hosted | {field: "unknown" if field == "startup_logging" else False}
                with self.assertRaises(ValueError):
                    preflight.validate_evidence(json.dumps(changed), PROD, digest, now=now,
                                                release=NEW, sql_snapshot=b"{}", expected_schema=b"{}")
            for field in ("effective_iis_config_checked", "schema_compatible", "runtime_compatible",
                          "startup_logging_verified"):
                changed = evidence | {field: False}
                with self.assertRaises(ValueError, msg=field):
                    preflight.validate_evidence(json.dumps(changed), PROD, digest, now=now,
                                                release=NEW, sql_snapshot=b"{}", expected_schema=b"{}")
            changed = evidence | {"backups": evidence["backups"] | {"database":
                                  evidence["backups"]["database"] | {"restorable": False}}}
            with self.assertRaises(ValueError):
                preflight.validate_evidence(json.dumps(changed), PROD, digest, now=now,
                                            release=NEW, sql_snapshot=b"{}", expected_schema=b"{}")

    def test_manual_sql_review_script_has_no_write_statement(self):
        script = Path("deployment/v2/manual-sql-review.sql").read_text()
        sql = "\n".join(line.split("--", 1)[0] for line in script.splitlines())
        self.assertFalse(re.search(r"\b(INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|EXEC|MERGE|TRUNCATE)\b", sql, re.I))
        self.assertIn("DB_NAME()", sql)

    def test_manual_sql_snapshot_checks_release_schema_and_migrations(self):
        from datetime import datetime, timezone
        now = datetime(2026, 11, 14, 19, tzinfo=timezone.utc)
        digest = "a" * 64
        columns = []
        for name, (kind, nullable) in manual_sql.REQUIRED_COLUMNS.items():
            schema, table, column = name.split(".")
            columns.append(dict(schema=schema, table=table, column=column, type=kind, nullable=nullable,
                                max_length_bytes=manual_sql.EXPLICIT_LENGTHS.get(name, 0) * 2))
        for name, length in manual_sql.EXPLICIT_LENGTHS.items():
            if name not in manual_sql.REQUIRED_COLUMNS:
                schema, table, column = name.split(".")
                columns.append(dict(schema=schema, table=table, column=column, type="nvarchar",
                                    nullable=True, max_length_bytes=length * 2))
        for name, default in manual_sql.REQUIRED_DEFAULTS.items():
            schema, table, column = name.split(".")
            entry = next((x for x in columns if (x["schema"], x["table"], x["column"]) ==
                          (schema, table, column)), None)
            if entry is None:
                entry = dict(schema=schema, table=table, column=column, type="nvarchar",
                             nullable=False, max_length_bytes=0)
                columns.append(entry)
            entry["default_definition"] = "('" + default + "')"
        indexes = []
        for name, unique in manual_sql.REQUIRED_INDEXES.items():
            schema, table, index = name.split(".")
            indexes.append(dict(schema=schema, table=table, name=index, unique=unique,
                                primary_key=False,
                                key_rows=[{"name": key} for key in manual_sql.REQUIRED_INDEX_KEYS[name]],
                                filter=("Provider = 'WebsiteCash'" if "WebsiteCash" in index else
                                        "Provider = 'StripeCheckout'") if table == "Payments" else None))
        indexes.append(dict(schema="dbo", table="Refunds", name="PK_Refunds", unique=True,
                            primary_key=True, key_rows=[{"name": "Id"}], filter=None))
        snapshot = dict(checked_at="2026-11-14T18:00:00Z", database="anisedatabase", server="SQLHOST",
                        columns=columns, indexes=indexes,
                        foreign_keys=[dict(parent_table="Refunds", parent_column="OrderId",
                                           referenced_table="Orders", referenced_column="Id")],
                        check_constraints=[dict(table=table, definition=" AND ".join(parts))
                                           for table, groups in manual_sql.REQUIRED_CHECKS.items()
                                           for parts in groups],
                        migration_history=dict(available=False, records=[]))
        expected = dict(columns=["dbo.Refunds.Id"], indexes=["dbo.Refunds.IX_Refunds_OrderId_CreatedAtUtc"],
                        mappedTypes=[dict(name="dbo.Refunds.Id", type="uniqueidentifier", nullable=False)],
                        mappedIndexes=[dict(name="dbo.Refunds.IX_Refunds_OrderId_CreatedAtUtc", unique=False,
                                            keys=["OrderId", "CreatedAtUtc"])],
                        mappedForeignKeys=[dict(parent_table="Refunds", parent_column="OrderId",
                                                referenced_table="Orders", referenced_column="Id")],
                        mappedPrimaryKeys=[dict(table="Refunds", keys=["Id"])])
        raw = json.dumps(snapshot).encode()
        model_raw = json.dumps(expected).encode()
        checksums = json.loads(Path("database/migration-checksums.json").read_text())
        review = dict(mode="manual", artifact_sha256=digest, release_commit=NEW,
                      snapshot_sha256=hashlib.sha256(raw).hexdigest(),
                      expected_schema_sha256=hashlib.sha256(model_raw).hexdigest(),
                      reviewed_at="2026-11-14T18:30:00Z", reviewed_by="owner",
                      record_reference="private/sql/review-2026-11-14",
                      approval="APPROVE POS PRODUCTION SQL PREFLIGHT",
                      database="anisedatabase", connection_host="mssqluk20.prosql.net",
                      observed_server="SQLHOST", migration_checksums=checksums, pending_migrations=[])
        self.assertTrue(manual_sql.validate(raw, model_raw, review, digest, NEW, now=now))
        with self.assertRaises(ValueError):
            manual_sql.validate(raw, model_raw,
                                review | {"release_commit": manual_sql.BASELINE_RELEASE},
                                digest, manual_sql.BASELINE_RELEASE, now=now)
        for field, bad in (("database", "anisedb"), ("server", "other"),
                           ("checked_at", "2026-11-10T18:00:00Z")):
            changed = snapshot.copy()
            changed[field] = bad
            changed_raw = json.dumps(changed).encode()
            changed_review = review | {"snapshot_sha256": hashlib.sha256(changed_raw).hexdigest()}
            with self.assertRaises(ValueError, msg=field):
                manual_sql.validate(changed_raw, model_raw, changed_review, digest, NEW, now=now)
        for field in ("columns", "indexes", "foreign_keys"):
            changed = snapshot.copy()
            changed[field] = []
            changed_raw = json.dumps(changed).encode()
            changed_review = review | {"snapshot_sha256": hashlib.sha256(changed_raw).hexdigest()}
            with self.assertRaises(ValueError, msg=field):
                manual_sql.validate(changed_raw, model_raw, changed_review, digest, NEW, now=now)
        changed = snapshot | {"migration_history": {"available": True, "records": []}}
        changed_raw = json.dumps(changed).encode()
        with self.assertRaises(ValueError):
            manual_sql.validate(changed_raw, model_raw,
                                review | {"snapshot_sha256": hashlib.sha256(changed_raw).hexdigest()},
                                digest, NEW, now=now)
        for field, bad in (("artifact_sha256", "0" * 64), ("migration_checksums", {}),
                           ("pending_migrations", ["011"]), ("reviewed_at", "2026-11-10T18:00:00Z"),
                           ("approval", ""), ("connection_host", "wrong.example")):
            with self.assertRaises((ValueError, KeyError), msg=field):
                manual_sql.validate(raw, model_raw, review | {field: bad}, digest, NEW, now=now)

    def test_routine_production_workflow_has_no_preflight_secret(self):
        workflow = Path("deployment/v2/workflows/pos-v2-production.yml").read_text()
        self.assertNotIn("sql_mode", workflow)
        self.assertNotIn("V2_SQL_CONNECTION", workflow)
        self.assertNotIn("PREFLIGHT_EVIDENCE", workflow)
        self.assertIn("schema_guard", workflow)

    def test_release_candidate_requires_manual_staging_run(self):
        self.assertTrue(resolve.accepted_branch({"head_branch": "staging", "event": "push"}))
        self.assertTrue(resolve.accepted_branch({"head_branch": "release/push-notifications", "event": "workflow_dispatch"}))
        self.assertTrue(resolve.accepted_branch({"head_branch": "release/push-production-safe", "event": "workflow_dispatch"}))
        self.assertFalse(resolve.accepted_branch({"head_branch": "release/push-notifications", "event": "push"}))
        self.assertFalse(resolve.accepted_branch({"head_branch": "release/push-production-safe", "event": "push"}))
        self.assertFalse(resolve.accepted_branch({"head_branch": "other", "event": "workflow_dispatch"}))

    def test_schema_guard_blocks_runtime_database_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            current, candidate = Path(folder) / "current", Path(folder) / "candidate"
            for root in (current, candidate):
                (root / "src/AnisePOS.Api/Data").mkdir(parents=True)
                (root / "src/AnisePOS.Api/Program.cs").write_text("startup")
                (root / "src/AnisePOS.Api/Data/AniseDbContext.cs").write_text("mapping")
            self.assertTrue(schema_guard.check(current, candidate))
            (candidate / "src/AnisePOS.Api/Program.cs").write_text("startup" + schema_guard.PUSH_TEST_RATE_LIMIT)
            self.assertTrue(schema_guard.check(current, candidate))
            (candidate / "src/AnisePOS.Api/Program.cs").write_text("ALTER TABLE")
            with self.assertRaisesRegex(ValueError, "Database requirements changed"):
                schema_guard.check(current, candidate)

    def test_caller_schema_release_requires_exact_approval_artifact_and_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            current, candidate = Path(folder) / "current", Path(folder) / "candidate"
            for root in (current, candidate):
                (root / "src/AnisePOS.Api").mkdir(parents=True)
                (root / "database").mkdir()
                (root / "src/AnisePOS.Api/Program.cs").write_text("old")
            (candidate / "src/AnisePOS.Api/Program.cs").write_text("new")
            for name in ("012_caller_events.sql", "013_caller_lifecycle.sql"):
                (candidate / "database" / name).write_text("CREATE TABLE")
            approval, release = schema_guard.CALLER_APPROVAL, schema_guard.CALLER_RELEASE
            self.assertTrue(schema_guard.check(current, candidate, approval, release))
            for bad_approval, bad_release in (("", release), (approval, "0" * 40)):
                with self.assertRaisesRegex(ValueError, "Database requirements changed"):
                    schema_guard.check(current, candidate, bad_approval, bad_release)
            (candidate / "database/014_unrelated.sql").write_text("ALTER TABLE")
            with self.assertRaisesRegex(ValueError, "Database requirements changed"):
                schema_guard.check(current, candidate, approval, release)

    def test_protected_manual_snapshot_expansion_is_bounded_and_private(self):
        raw = b'{"database":"anisedatabase","columns":[]}'
        encoded = base64.b64encode(zlib.compress(raw)).decode()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "snapshot.json"
            load_manual_snapshot.load(encoded, path)
            self.assertEqual(path.read_bytes(), raw)
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            with self.assertRaises(FileExistsError):
                load_manual_snapshot.load(encoded, path)
            with self.assertRaises(ValueError):
                load_manual_snapshot.load(base64.b64encode(zlib.compress(b'{"database":"anisedb"}')).decode(),
                                          Path(folder) / "wrong.json")

    def test_finite_sql_length_exception_is_explicit(self):
        observed = {"type": "nvarchar", "max_length_bytes": 60}
        self.assertTrue(manual_sql._compatible_type("nvarchar(max)", observed, "dbo.Products.VeganSection"))
        self.assertFalse(manual_sql._compatible_type("nvarchar(max)", observed, "dbo.Promotions.SettingsJson"))

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
