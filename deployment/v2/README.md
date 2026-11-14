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
   artifact. A protected environment approval gates the job. The read-only SQL
   probe checks the connected database and mapped schema without starting the
   API. Fresh protected evidence must identify the effective IIS site/config,
   SQL identity, runtime, working secure startup logging, schema compatibility,
   and two distinct retained/restorable application and SQL backups. The engine
   verifies and archives all current managed files before uploading. No SQL is
   changed. No release is called healthy before the retried health response.
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
  "sql_server": "SERVER_RETURNED_BY_READ_ONLY_SQL_PROBE",
  "sql_database": "anisedatabase",
  "artifact_sha256": "64_HEX_CHARACTERS_FROM_PACKAGE_MANIFEST",
  "effective_iis_config_checked": true,
  "sql_readonly_probe_passed": true,
  "schema_compatible": true,
  "runtime_compatible": true,
  "startup_logging_verified": true,
  "pending_sql_migrations": [],
  "backups": {
    "application": {"reference": "PRIVATE_APP_BACKUP_ID", "retained": true, "restorable": true},
    "database": {"reference": "PRIVATE_SQL_BACKUP_ID", "retained": true, "restorable": true}
  }
}
```

These booleans are attestations, not automatic Winserve or backup-provider
checks. Before activation, an authorised operator must verify the **effective**
IIS `web.config`, inherited/site variables and `ConnectionStrings__AniseDatabase`,
read-only SQL login and `DB_NAME()`/`@@SERVERNAME`, .NET runtime, actual stdout
logging location/permissions/retention, and restorable private backups. Do not
place a connection string, password or log contents in this JSON. The workflow
also runs `DbProbe` against SQL with a protected read-only login, verifies its
identity against this evidence, checks that the probe login lacks write access
to mapped tables, and fails on missing mapped columns or the required payment
index. The protected schema attestation must also cover types, constraints,
indexes and migration history; the probe is not a full schema diff. It does
not execute the API's startup SQL.

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
