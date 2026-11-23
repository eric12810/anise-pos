# POS branch reconciliation review — 23 November 2026

This is an investigation record, not approval to merge or deploy. The live POS applications were not changed.

## Verified release records

| Item | Commit | Evidence |
| --- | --- | --- |
| Latest successful V2 staging deployment | `0761027bfa6d9d608bf6a89e8695eb557ab616b3` | [Run 45253210471](https://github.com/eric12810/anise-pos/actions/runs/45253210471), deploy job succeeded 23 November 2026 at 19:55 UTC. |
| Latest successful V2 production release | `7ef20b0f0c229afe3d17bb62f3cab95ee2fcd51b` artifact from staging run 45041553292 | [Run 45042812468](https://github.com/eric12810/anise-pos/actions/runs/45042812468), release job resolved and installed this artifact 22 November 2026 at 16:21 UTC. The production workflow ran at `13e4cd5262305318b00e1ae6d10c1cf7820585e7`, which is not the artifact source. |
| Current `main` head | `481ccd37caf8cdc443f45d19ef9c562e2815d580` | Fetched `origin/main`. |
| Current `staging` head | `0761027bfa6d9d608bf6a89e8695eb557ab616b3` | Fetched `origin/staging`. |

The run records establish the last successful deployments, but no read-only hosted manifest comparison was performed here. A later manual rollback or out-of-band upload would require separate verification.

Local backup tags preserve the two branch heads and the production artifact commit: `backup/reconcile-2026-11-23-main`, `backup/reconcile-2026-11-23-staging`, and `backup/reconcile-2026-11-23-production-artifact`. These tags are local and have not been pushed. The checked-out staging worktree was clean before investigation. Other existing local worktrees were left intact.

## Branch differences

At the start of this review, the merge base was `bbfd19864093158a8d2123cc34bae6c97eb92049`. `main` had 37 commits absent from `staging`; `staging` had 44 absent from `main`. Sixty-eight files differed. The following bullets record that initial comparison; see current status below.

* Application and POS UI: staging has newer card and Stripe entry controls, ETA and order edits, caller notifications and lifecycle UI, and end-of-day external Stripe takings. These need feature-level review before moving source between branches. The current production artifact predates the last two staging commits; its tracked application source differs from current staging only in `wwwroot/end-of-day.html` and `wwwroot/reports.html`.
* Database: staging adds caller event and lifecycle migrations `012` and `013` and production review SQL. `main` lacks these files. Their application and schema dependencies prevent a blind source merge. No database operation was performed.
* Deployment: `main` has the active `pos-v2-production.yml` and `pos-v2-rollback.yml`, both absent from staging. Staging has five legacy production workflows recently deleted on `main`. The V2 staging workflow differs materially: staging uses the newer `staging-deploy` and manual recovery path; main uses an older backup/deploy path. The production workflow on main is manually dispatched, gated by the production environment, and resolves a staging artifact. Its schema guard and rollback path depend on main-side deployment modules that differ from staging. The legacy staging production workflow is also manually dispatched and uses a different uploader; it should remain inactive pending explicit retirement review.
* Tests and documentation: staging has additional JavaScript cases for caller navigation, payment controls, printing and address saving. V2 Python tests differ substantially. The two V2 workflow copies under `deployment/v2/workflows/` also diverge and need an explicit canonical-source decision.

## Remaining review

1. Read the live V2 deployment state or hosted release manifest without starting the API, and compare the release and artifact hash to the successful runs above.
2. Preserve both branch heads remotely with names that do not match deployment triggers; the initial backup tags are local only.
3. Review PR #42. Then reconcile deployment tooling and workflow copies without replacing staging's working deploy/recovery path or production's manual gate, exact artifact resolution, pre-release backup, schema guard, and rollback. Review legacy workflow removal separately. A merge into `staging` would trigger its automatic deployment, so prepare that PR without merging it.
4. Review the two staging-only reporting changes as a future production release candidate through the normal staging-artifact process. Do not copy them directly into `main` as though they were already deployed to production.
5. Validate .NET, JavaScript, Python, migration checks, workflow YAML, package manifest and environment targets on any later deployment-tooling candidate before merge.

## Validation and remaining gates

On the current staging checkout, `python3 scripts/check-migrations.py` passed its static file/checksum checks and 73 Python deployment tests passed. The migration checker explicitly did not inspect database history or pending state. Node.js subsequently became available: all 11 JavaScript tests passed locally. GitHub run 45253210471 reports successful .NET build and checks, JavaScript tests, Python tests, migration checks, package creation and staging deploy for the staging head. The first CI run for this review branch exposed two stale tests that referenced deleted legacy production workflows; the tests now assert the retained V2 production and rollback safeguards. All 85 Python deployment tests pass locally and the amended PR's CI check passed. PR #41's .NET restore and full GitHub build also passed.

[PR #41](https://github.com/eric12810/anise-pos/pull/41) merged on 23 November 2026. The `main` application, migration, and application test source now matches production artifact source commit `7ef20b0` for those paths, and the PR build, tests, and package validation passed. The two later staging reporting changes remain intentionally absent from `main`. This merge did not deploy either POS application or change a database.

The remaining application difference is limited to `wwwroot/end-of-day.html` and `wwwroot/reports.html`. Deployment workflows and modules still differ by environment. Draft [PR #42](https://github.com/eric12810/anise-pos/pull/42) narrows production artifact selection to the `staging` branch, removes the old manual release-branch staging path from the `main` workflow copy, and separates validation concurrency from staging deployment. Its GitHub build, tests, and package check passed; it has not merged. Branch histories remain divergent by design, and the deployment workflow differences require a separate review before declaring the repository fully reconciled.
