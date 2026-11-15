# POS Deployment V2 — design and activation record

V2 is implemented **alongside** the current deployment system. The three YAML files in
`deployment/v2/workflows/` are templates, not active GitHub Actions workflows. Nothing
in this directory has deployed to either Winserve site or connected to either SQL
database. Production currently returns HTTP 500.30 for the newest application; V2
does not diagnose or fix that startup exception.

## Assessment of the current system

The repository has seven deployment workflows, about a dozen deployment and
recovery scripts, and two deployment test modules. The staging workflow is the
useful baseline: it builds once, runs checks, validates and hashes a publish
package, uploads with curl explicit-TLS FTPS, downloads each file to verify its
hash, preserves hosted configuration, and stops on interrupted markers. The
existing production release resolves a successful staging run, but its offline
preflight checks source text and configured names; the backup gate is a flag,
the upload precedes a single `/health` request, and successful upload can be
recorded before runtime health. The recent baseline, diagnostic and numbered
rollback workflows exist because the production site and FTPS behavior needed
careful incident-specific recovery. Keep them until a V2 recovery has been
tested and explicitly accepted.

| Component | Decision |
| --- | --- |
| `scripts/build-web-assets.py`, package review and checksum generation | Retain during V2; strip generated `web.config` from the V2 artifact after review. |
| curl explicit TLS, `multicwd`, staging double-slash path, chrooted live path, relative `+RNFR/+RNTO` and `+DELE`, download hash verification | Retain in shared `transport.py`; test exact command behavior. |
| Current staging/production workflows and incident rollback tools | Keep active/available until approved cutover and a successful V2 staging test; retire only by a separate reviewed change. |
| Environment-specific upload and release scripts | Replace with `model.py`, `engine.py`, `transport.py`, `preflight.py`, `cli.py` after cutover. |
| SQL migration checksum review | Retain as offline check; future versioned migration runner needs its own approval and backup implementation. |

## V2 workflow

1. **Staging** builds, tests and packages one commit. It removes generated
   `web.config`, hashes all remaining files, retains the exact artifact, verifies
   the existing managed application archive, installs via the shared engine,
   waits for IIS to release files, then retries `/health` for up to about three
   minutes. `/health` must report both application and database OK. Only then is
   the release marked healthy. A failed install leaves `app_offline.htm` and the
   last healthy pointer intact.
2. **Production** resolves a successful V2 staging run and its unexpired exact
   artifact. A protected environment approval gates the job. A fresh, manually
   reviewed metadata-only SQL record checks database identity, mapped schema,
   index and migrations without starting the API or placing SQL credentials in
   GitHub Actions. Protected evidence must identify the effective IIS site/config,
   SQL identity, runtime, working secure startup logging, schema compatibility,
   and two distinct retained/restorable application and SQL backups. The engine
   verifies and archives all current managed files before uploading. No SQL is
   changed. No release is called healthy before the retried health response.
   For the **first** V2 production release only, select `first_release` and
   supply the exact `APPROVE FIRST POS V2 PRODUCTION RELEASE` phrase. The same
   protected workflow verifies the existing legacy production manifest and
   every listed live file, retains a checksum-verified archive before upload,
   and rechecks the live bytes against that archive immediately before install.
   The previous application is recorded as **unhealthy/health unverified**;
   it is never seeded as a healthy V2 release. A healthy V2 pointer is created
   only if the new release's application and database health checks pass.
   On failure, maintenance remains and the failed release is recorded. Recovery
   from the legacy archive then requires a separately reviewed manual action;
   the normal V2 rollback workflow cannot claim that unhealthy legacy state
   was healthy. This path leaves hosted configuration and SQL unchanged.
3. **Rollback** requires protected production approval, exact failed release
   SHA, and the pre-release application archive. It verifies the archive against
   the last healthy server pointer, verifies failed-release-only files before
   removing them, restores only managed application files, and checks health.
   It never modifies hosted configuration, SQL, customer/order/payment data,
   data-protection keys or runtime data. A database migration is not reversed
   by application rollback.

## Configuration and release state

`Target.validate()` fixes the site URL, FTP account/root and database name for
each environment. The host and password come only from that environment's
protected GitHub secrets. There is no staging fallback for production. The
published artifact contains no `web.config` or `appsettings*`; V2 requires the
hosted `web.config` to exist and leaves it untouched. The FTPS account is
restricted to the approved `wwwroot`; production and staging remain separate
even if Winserve uses the same FTP hostname.

Each release is identified by its 40-character Git commit SHA plus the SHA-256
of `release-manifest.sha256`. The remote `.anise-v2-<sha>.json` records phase and
transition history (`uploaded`, `installed`, `healthy`, `failed`).
`.anise-v2-healthy.json` keeps the last verified healthy release;
`.anise-v2-current.json` identifies the currently approved healthy application.
State publication uses a verified `.pending` upload and same-directory Winserve
rename. Leftover `.pending`, `.previous` or `app_offline.htm` blocks another
normal deployment for recovery review. GitHub artifacts alone are not treated
as installed or healthy release history.

## Protected evidence contract

`V2_PRODUCTION_PREFLIGHT_EVIDENCE` is a JSON object supplied through the
protected production environment. It is bound to the exact artifact checksum
and expires after 24 hours. Required fields are:

```json
{
  "checked_at": "2026-11-14T18:00:00Z",
  "environment": "production",
  "site": "https://pos.loveanise.co.uk",
  "ftps_host": "HOST_VERIFIED_IN_WINSERVE",
  "ftps_user": "liveposftp",
  "ftps_root": "/wwwroot",
  "sql_server": "SERVER_RETURNED_BY_MANUAL_READ_ONLY_QUERY",
  "sql_database": "anisedatabase",
  "artifact_sha256": "64_HEX_CHARACTERS_FROM_PACKAGE_MANIFEST",
  "effective_iis_config_checked": true,
  "sql_mode": "manual",
  "manual_sql_review": {
    "mode": "manual",
    "reviewed_at": "FRESH_UTC_TIME_AFTER_QUERY",
    "reviewed_by": "OWNER_ID",
    "record_reference": "private/sql/review-YYYY-MM-DD",
    "approval": "APPROVE POS PRODUCTION SQL PREFLIGHT",
    "connection_host": "mssqluk20.prosql.net",
    "observed_server": "VALUE_OF_@@SERVERNAME",
    "database": "anisedatabase",
    "release_commit": "bbfd19864093158a8d2123cc34bae6c97eb92049",
    "artifact_sha256": "0041791ecf107370e054202ba3d4e9961b003c8908e985f21b06ff2784882c7b",
    "snapshot_sha256": "SHA256_OF_PRIVATE_SQL_JSON_FILE",
    "expected_schema_sha256": "SHA256_OF_OFFLINE_RELEASE_MODEL_JSON",
    "migration_checksums": {
      "010_order_lifecycle.sql": "6b8bda54f01669104f3bad0f8dcadedfa0e4f9f280c7d520963c0b278b005d7f",
      "011_website_stripe_reconciliation.sql": "28cb172e7587a2a0d0fa04af4296db235d0ab543a9339a9b08c37c9e6ca43e93"
    },
    "pending_migrations": []
  },
  "schema_compatible": true,
  "runtime_compatible": true,
  "startup_logging": "verified",
  "startup_logging_verified": true,
  "pending_sql_migrations": [],
  "backups": {
    "application": {"reference": "PRIVATE_APP_BACKUP_ID", "retained": true, "restorable": true},
    "database": {"reference": "PRIVATE_SQL_BACKUP_ID", "retained": true, "restorable": true}
  }
}
```

If Winserve cannot provide writable stdout logs or ANCM startup logs, record
`"startup_logging": "host_unavailable"`, `"startup_logging_verified": false`,
and `"startup_logging_limitation_acknowledged": true`. This is an explicit
diagnostic limitation, not a claim that logging works. Post-deploy health and
failed-release recovery requirements remain in force.

The manual path takes the complete JSON result from `manual-sql-review.sql` and
an offline EF schema generated from the **exact release source**. It checks
all mapped tables, columns, SQL types, nullability, primary keys, indexes and
foreign keys, plus the explicit 010/011 structures and checked-in checksums.
If the migration-history table exists, its version/checksum rows must be added
to the private snapshot and match the reviewed checksums; an absent history
table is recorded as absent, not treated as proof of applied migrations. The
010/011 structures are always checked. Snapshot and review must both be less
than 24 hours old; the SHA-256 values cover the exact private files. This is
reviewed operator evidence, not an authenticated observation by GitHub and not
proof that the existing SQL account is permission restricted.

In VS Code, select the existing production MSSQL connection, open
`deployment/v2/manual-sql-review.sql`, and run the **whole file**. Check that
the returned JSON says `database: anisedatabase`, save the entire JSON cell as
a private UTF-8 `sql-snapshot.json` outside the repository with mode `0600`.
Never add it to Git. If
`migration_history.available` is true, inspect only that table's version and
checksum columns with a separate read-only `SELECT`, add those records to the
private JSON, and recheck its hash. Do not include application rows. The query
does not start the API, execute migrations, or change the database. The
connection host is recorded by the reviewer because `@@SERVERNAME` can be an
instance name rather than the DNS host.

Generate `expected-schema.json` from the verified release commit
`bbfd19864093158a8d2123cc34bae6c97eb92049` with the offline
`DbProbe --schema-json` command. The dormant production template checks out
that exact commit and runs the current offline helper against its API source.
For a local preflight, use an isolated checkout of the same commit; place the
current `DbProbe/Program.cs` in its `DbProbe` directory before running it.
Record SHA-256 of both JSON files in the private review. Populate the remaining
Winserve, runtime, logging and backup attestations only after independent
verification. The preflight-only command is:

```sh
python3 -m deployment.v2.cli preflight --environment production \
  --artifact artifacts/staging --release bbfd19864093158a8d2123cc34bae6c97eb92049 \
  --sql-mode manual --evidence /private/tmp/anise-v2-preflight/preflight.json \
  --sql-snapshot /private/tmp/anise-v2-preflight/sql-snapshot.json \
  --expected-schema /private/tmp/anise-v2-preflight/expected-schema.json
```

This command reads local files and exits before constructing FTPS transport.
Its automated counterpart selects `--sql-mode automated` and supplies
`--db-probe` from the existing `DbProbe --output` operation. The automated
workflow branch requires a separately verified read-only SQL credential and
checks the probe's application-table write permission result. Manual mode has
no SQL credential in GitHub Actions. The full production workflow template
remains dormant; an eventual deployment still requires production environment
approval, `V2_ACTIVATED=yes`, verified backups, and healthy application checks.
The raw SQL snapshot may exceed GitHub's single-secret size limit. The dormant
workflow accepts only a zlib-compressed, base64-encoded copy in the protected
production environment secret `V2_PRODUCTION_SQL_SNAPSHOT_ZLIB_B64`; the runner
expands it into a new mode-0600 file with a 1 MB limit. To prepare that value
privately, use Python to write `base64.b64encode(zlib.compress(snapshot_bytes, 9))`
to a mode-0600 file outside the repository, then supply its contents through
the protected GitHub secret interface. Never print it in CI logs or commit it.
The SHA-256 in `manual_sql_review` covers the **uncompressed** JSON bytes.
For the current 79,952-byte snapshot, the encoded compressed value is 4,748
bytes. Do not activate the workflow until all other release gates pass.

Startup exception collection remains a hosting-side operation: Winserve must
confirm a private writable log directory outside public `wwwroot`, log rotation
and restricted access; the relevant exception should be attached to a private
incident record, with secrets and customer data redacted. A workflow should
report the log reference and failure phase, never publish raw startup logs.
The unresolved production HTTP 500.30 exception must be investigated separately.

## Migration and activation

V2 does not run SQL migrations. A future integrated runner should keep an
`AniseSchemaMigrations` table with migration ID, immutable checksum, applied UTC
and release ID; compare live schema/history with checked-in migrations; fail on
changed checksums or gaps; apply reviewed pending changes to staging first;
require a fresh restorable SQL backup and explicit production approval; and
separate non-backward-compatible changes into expand/migrate/contract releases.
The release workflow currently rejects nonempty `pending_sql_migrations`.
The application-only rollback must never claim to reverse a migration.

Before any cutover: obtain the Winserve startup exception; verify protected
staging and production host/SQL identities and isolated credentials; confirm
private startup logs and a tested database backup/restore; run all offline
tests and a disposable staging FTPS rename/upload/download/delete test; seed a
V2 baseline from a byte-identical healthy artifact using the explicit `seed`
command; then activate only the staging workflow in a reviewed change. Test
staging failure and recovery with disposable data, verify the website's staging
menu/images/order compatibility, and request explicit approval before replacing
production workflows. The old and V2 staging push workflows must not both be
active during cutover. Keep the historical rollback scripts and backups until
the V2 production rollback has been rehearsed and accepted.

The 90-day GitHub artifacts are an immediate recovery copy, not a durable
backup. Protected evidence must point to separately retained, restorable
application and SQL backups. The current rollback template downloads its archive
from the producing Actions run; before production activation, add and rehearse a
protected retrieval path from the durable application backup store so rollback
still works after Actions artifact expiry. No production deployment, rollback,
SQL mutation, hosting configuration change or V2 workflow activation is part of
this branch.

The V2 transport probe is `python3 -m deployment.v2.cli ftps-probe --environment staging`
with the protected staging FTPS environment and `V2_FTPS_PROBE_APPROVAL=PROBE STAGING FTPS`.
It writes, verifies, renames and deletes one random disposable staging file.
It was not run against Winserve in this development branch.
