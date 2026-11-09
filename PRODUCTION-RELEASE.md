# POS production release procedure

The staging dry-run publishes the exact reviewed package as a retained artifact. Production accepts only a selected artifact/run and never rebuilds it. `production-preflight.py` inspects `Program.cs` startup SQL and numbered scripts offline; it does not start the API or connect to SQL.

Run the production workflow with the exact successful staging dry-run ID. The workflow resolves the matching artifact and commit through the GitHub API, then requires the protected `production` environment reviewer, separate FTPS credentials, exact approved host/root and database variables, and the protected `PROD_BACKUP_CONFIRMED=true` production variable. Database identity must have been verified by an authorised read-only session before enabling a release; this workflow does not perform that check. Existing `web.config`, appsettings, data and logs remain server-side, and no remote deletion is allowed.

Retain the previous artifact and deployment manifest. Roll back by selecting the previous approved artifact through the same guarded workflow after health-check failure review. Never use API startup to discover a production connection because startup SQL can alter schema.

Approved FTPS mapping supplied by the owner: host `pos.loveanise.co.uk`, account `liveposftp`, remote root `/liveposftp/wwwroot`. The workflow reads `LIVE_FTPS_HOST`, `LIVE_FTPS_USERNAME`, and `LIVE_FTPS_PASSWORD` from repository secrets and validates this exact root.
