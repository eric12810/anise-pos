# POS GitHub and staging setup

The workflow is an offline package review only. It builds the API, runs the
order-transition checks, publishes an IIS package and lists file hashes in the
Actions log. It has no upload step, FTPS credentials or database connection.
It does not start the API. Package output is ignored by Git.

## Repository and confirmed destination

Private repository: `eric12810/anise-pos`. Both `main` and `staging` are published.
Work on `staging`; pushing runs the offline package review, never an upload.

Confirmed by the user:
- Site: `https://pos-staging.loveanise.co.uk`.
- SolidCP Home Folder: `\pos-staging.loveanise.co.uk\wwwroot`.
- FTPS host: `wsuk32.pronameserver.com`, port 21, explicit TLS.
- Account: `stageposftp`, login root `/`, mapped to the staging site root.
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

Uploads overwrite package files only. Existing `web.config`, all appsettings,
unrelated files, and runtime data are preserved. If no web.config exists, the
published IIS configuration is installed. Confirm that existing web.config runs
`AnisePOS.Api.dll` with the .NET runtime before deployment. Confirm the host has
the appropriate ASP.NET Core hosting runtime and the application can write its
sibling `data` directory. Keep staging payment and email settings isolated.

The uploader creates `app_offline.htm`, waits for IIS to release assemblies,
then uploads and downloads each package file to verify its SHA-256 hash. After
success it removes only the maintenance marker it created, allowing IIS to
restart the application. Starting the API can update the database schema.
An existing maintenance marker causes an abort before any write.

If interrupted or failed after upload begins, staging stays offline. Do not
blindly remove the marker: some files may be from different versions. Restore a
known-good hosting backup or complete a reviewed recovery before restarting.
There is no automatic rollback or remote cleanup. Confirm a recoverable backup
of existing staging application files before the first upload, as well as the
database backup. After deployment, check `/health` and staff login. A successful
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
