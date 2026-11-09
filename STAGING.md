# POS GitHub and staging setup

Pushing to staging runs an offline package review: the .NET build, application tests, deployment safeguards, package hashes and publish output. This dry run does not connect to hosting, start the API or modify the database. Separately, the manually triggered POS staging FTPS workflow supports read-only connection check and explicitly confirmed deployment. Package output is ignored by Git.

## Repository and confirmed destination

Private repository: `eric12810/anise-pos`. Both `main` and `staging` are published.
Work on `staging`; pushing runs the offline package review, never an upload.

Confirmed by the user:
- Site: `https://pos-staging.loveanise.co.uk`.
- SolidCP Home Folder: `\pos-staging.loveanise.co.uk\wwwroot`.
- FTPS host: `wsuk32.pronameserver.com`, port 21, explicit TLS.
- Account: `stageposftp`, login folder `/stageposftp`; deployment folder `/stageposftp/wwwroot`.
  Sibling `data` and `logs` folders are outside deployment.
- `anisedb` backed up; staging configured for `anisedb`, not `anisedatabase`.
- The three `STAGING_FTPS_*` repository secrets have been saved in GitHub.

## Manual connection check and upload

In Actions choose **POS staging FTPS**, **Run workflow**, branch **staging**.
Leave mode **check** first. This validates the TLS certificate and encrypted
connection and directory listing without writing any remote files. It does not
prove the IIS runtime, database, email or payment configuration is correct.

Only after reviewing that result and approving deployment, select **deploy** and
enter exactly `DEPLOY pos-staging.loveanise.co.uk` in confirmation. The workflow
rejects any other branch and serializes runs. This manual confirmation is not a
protected-environment reviewer policy: repository writers can invoke it.

On first use, the incremental uploader has no remote deployment manifest and uploads the complete reviewed package once. After successful upload and SHA-256 download verification, it records the deployed hashes in `.anise-pos-staging-deployment.json`. Later deployments upload only changed package files. If there are no changes, it does not create the IIS maintenance marker or restart the API. It refuses to continue if previously managed files disappear from the new package: review compatibility and file removals manually. It does not delete obsolete remote application files.\n\nUploads overwrite approved package files only. Existing `web.config`, all appsettings,
unrelated files, and runtime data are preserved. If no web.config exists, the
published IIS configuration is installed. Confirm that existing web.config runs
`AnisePOS.Api.dll` with the .NET runtime before deployment. Confirm the host has
the appropriate ASP.NET Core hosting runtime and the application can write its
sibling `data` directory. Keep staging payment and email settings isolated.

The uploader creates `app_offline.htm`, waits for IIS to release assemblies,
then uploads and downloads each package file to verify its SHA-256 hash. After
success it removes only the maintenance marker it created, allowing IIS to
restart the application. Starting the API can update the database schema.
An existing maintenance marker (including one left by our own failed run) or interrupted manifest publication causes an abort before any write. Recovery requires explicit review; no automatic resume. The deployment manifest is not a checksum of the live server and does not detect out-of-band FTP edits; deploy only through the guarded workflow.

If interrupted or failed after upload begins, staging stays offline. Do not
blindly remove the marker: some files may be from different versions. Restore a
known-good hosting backup or complete a reviewed recovery before restarting.
There is no automatic rollback or remote cleanup. Confirm a recoverable backup
of existing staging application files before the first upload, as well as the
database backup. After deployment, check `/health`, `/api/menu`, staff login and website staging requests (including CORS). A successful
file upload alone does not establish that the application works.

## Local verification

```sh
dotnet restore AnisePOS.sln
dotnet build AnisePOS.sln --configuration Release --no-restore
dotnet run --project tests/AnisePOS.Checks --configuration Release --no-build
dotnet publish src/AnisePOS.Api/AnisePOS.Api.csproj --configuration Release --no-restore --no-self-contained -p:UseAppHost=false --output artifacts/staging
python3 scripts/review-staging-package.py
```

The review removes appsettings files only from the generated package. Original
local configuration stays intact. This is a file-selection review, not an FTPS
connection test or proof that the site will run with the hosting configuration.
