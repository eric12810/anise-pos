# POS GitHub and staging setup

The workflow is an offline package review only. It builds the API, runs the
order-transition checks, publishes an IIS package and lists file hashes in the
Actions log. It has no upload step, FTPS credentials or database connection.
It does not start the API. Package output is ignored by Git.

## Private repository

Create a separate private POS repository (proposed name: `eric12810/anise-pos`).
In VS Code, use Source Control to commit the reviewed files and Publish to GitHub,
choosing a private repository. Keep local `appsettings*.json` files ignored.
Use `main` as the initial branch, then create and publish `staging`.
Pushing `staging` runs the dry run; it can also be run manually from Actions once
the workflow is on the default branch.

## Before enabling uploads

- Intended site: `https://pos-staging.loveanise.co.uk`.
- Staging account: `stageposftp`. Its observed login path is `/`; this is a
  candidate destination, pending confirmation against SolidCP's Home Folder.
  Do not append `wwwroot` without confirming the actual mapping.
- Confirm FTPS hostname, port, TLS mode, valid TLS certificate and account
  restriction to the staging POS folder.
- Back up `anisedb` and verify the staging connection points to it, never the
  live `anisedatabase`. Starting this API executes schema-changing SQL.
- Confirm the host supports this project's .NET 9 runtime and IIS hosting module.
- Configure staging-only settings on the host, including the database connection,
  test payment credentials and suitable email settings. Keep these out of Git.
- Confirm the application can persist data-protection keys in its configured
  sibling `data` directory and keep that directory outside deployment sync.
- Review the manifest and generated `web.config` before the first upload.

When these are verified, store only staging FTPS credentials as GitHub Actions
secrets (for example `STAGING_FTPS_HOST`, `STAGING_FTPS_USERNAME` and
`STAGING_FTPS_PASSWORD`). A future upload workflow must require explicit approval,
preserve server settings and runtime data, and avoid remote deletion. No live
credentials or production deployment are included in this setup.

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
