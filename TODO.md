# Anise — POS & Website To-do List

Shared task list for the Anise POS and customer website. This file lives in the Anise POS repository, but tracks work for both projects. Tick tasks off as they are completed.

## POS

### Future order lifecycle work

- [ ] **End of Day Cancellation and Refund Compatibility** — Exclude unpaid cancelled orders from completed sales; record refunds on their actual transaction dates; preserve historical totals; break down refunds by payment method and platform; include cash refunds in till reconciliation; avoid double-counting marketplace figures; support refunds for prior trading days; distinguish gross sales, refunds and net sales.
- [ ] **Staff Permissions and Manager Approval** — Add configurable staff permissions and optional manager PIN approval for cancellations, full refunds, partial refunds and reopening completed orders, with staff-level audit records.

### Telephone orders and payments

- [ ] **Add “Copy & Open Stripe” button**
  - Keep the existing **Copy Total** button unchanged.
  - Copy the exact payable order total to the iPad clipboard.
  - Open `https://dashboard.stripe.com/` in a new Safari tab or window without replacing the current POS order.
  - Staff can use Safari's **Open in Stripe app** banner, where available; a direct app-opening link has not been verified.
  - Preserve the order when staff return to the POS.
  - **Never automatically mark an order paid** just because Stripe has opened. Staff should confirm the successful payment in Stripe before manually recording it in POS.
  - Test on the shop iPad: clipboard, Safari pop-up behaviour, app banner, returning to POS, cancelled payments and failed payments.

#### Implementation notes

- This is a convenience shortcut for the existing telephone-payment workflow, not a Stripe MOTO API integration.
- Do not collect, store or log customers' card details in Anise POS.

### End-of-day reporting and bookkeeping

- [ ] **Persist end-of-day platform takings and daily closing reports in SQL Server**
  - Replace the current printout-only `sessionStorage` workflow with a server-side saved closing record for each **trading date**. Keep the existing end-of-day preview and print format working.
  - Save the manually entered **gross takings** separately for **Just Eat, Uber Eats, Deliveroo and Other platforms**, alongside a snapshot of the Anise POS order/payment totals used for the closing report. Do not treat net platform payouts as gross sales.
  - Allow authorised staff to retrieve previous closing records by date, view the stored platform breakdown, and **reprint** the report without retyping figures. Add **CSV export** for bookkeeping and future accounting integration.
  - Allow corrections without silently overwriting history: record original and revised values, editor, timestamp and optional reason; use appropriate Owner/Manager permissions and preserve an audit trail.
  - Prevent double-counting: distinguish orders already recorded in POS from externally entered platform totals before calculating **combined takings**. Clearly label POS sales, external sales and totals, and don't add the same order twice.
  - Decide how repeat saves, incomplete days, late adjustments and missing/zero entries behave. Use date-unique closing records or explicit revisions rather than accidental duplicate daily records.
  - Prepare a **staging-only** database migration and tests for saving, refreshing, retrieving, editing, printing, exporting and avoiding double counts. Keep staging and production databases separate; **do not migrate or modify production without explicit approval**.

## Website

_No website-only tasks recorded yet. Add them here as they arise._

## Shared / Infrastructure

### Deployment safety and recovery

- [ ] **Integrate version-tracked SQL migrations into the POS production release workflow**
  - Detect pending numbered, reviewed SQL migrations and track applied versions/checksums in a dedicated migration-history table; refuse changed scripts, duplicate versions or unexpected schema drift.
  - Validate the exact target database and credentials before any SQL change: production must be `anisedatabase` via a dedicated production migration identity; staging must remain `anisedb`. Never copy live customer, order or payment data to staging.
  - Test migrations against staging first, including repeat/no-op execution, failure recovery and compatibility with both the current live application and the incoming release.
  - Before applying production migrations, require an approved release, a **fresh recoverable production SQL backup verified by a separate backup mechanism**, and an ordering/write freeze when required. Fail closed if any prerequisite cannot be verified; do not rely on a manually set backup flag alone.
  - Apply only approved pending migrations in order with appropriate transaction/rollback handling, run schema and data-integrity postchecks, and **stop application deployment on any migration failure**. Do not silently use startup-time schema changes as a substitute.
  - Separate potentially destructive or non-backward-compatible changes for explicit manual review and a staged expand/migrate/contract plan. Application rollback does not automatically reverse database migrations.
  - Integrate this into the existing protected production release action rather than requiring a separate routine manual SQL step; skip cleanly when no migrations are pending. Keep database access credentials in protected secrets and never print them.
  - Implement **after the current first production release is stable**, not as an untested change to the in-progress release.

- [ ] **Automate pre-deployment backups for the live website and POS**
  - Add a backup step before each production deployment in the relevant GitHub Actions workflows.
  - Retrieve the current live website and POS files over FTPS, keeping their backups separate.
  - Save a timestamped backup with the environment, Git commit and file manifest/checksums, in private storage with an appropriate retention policy.
  - Fail closed: **do not start uploading the new release unless the full backup has completed and passed verification**.
  - Protect secrets and sensitive configuration; encrypt backups when required, restrict access and keep credentials out of logs.
  - Test backup and manual restore procedures on staging before relying on them in production.
  - Document a controlled manual rollback procedure; do not trigger automatic production rollback.

- [ ] **Plan and verify separate production SQL database backups**
  - Confirm a supported database-backup method with the SQL Server hosting provider and which account can perform it.
  - Back up production data independently of the website/POS file backups; document retention, encryption, access and recovery.
  - Test restoring to a separate non-production database, never over the production database during verification.
  - Decide whether database backup verification should be a mandatory deployment gate once implemented.

- [ ] **Add automated post-deployment production smoke tests for website and POS**
  - Integrate checks into both live GitHub Actions workflows after a successful deployment; also allow manual runs.
  - Verify HTTPS, expected pages, essential CSS/JS assets, safe read-only APIs, menu data and representative dish images.
  - Use a real browser test (for example Playwright) to detect JavaScript errors, CORS problems, broken image loads and accidental staging API/image URLs on production.
  - Check POS login-page availability and, where appropriate, a restricted, read-only application/database health endpoint.
  - Where feasible, confirm the live build/release marker matches the commit just deployed.
  - Apply sensible timeouts, retries and clear diagnostics; mark smoke-test failures in GitHub Actions and retain logs.
  - **Never create live orders, initiate payments, change stock, or write to production data as part of smoke tests.**
  - Validate tests on staging first; report production failures without automatically redeploying or rolling back.

### Emergency hosting and business continuity

- [ ] **Prepare a Mac-based emergency hosting setup for Anise website and POS**
  - Treat this as a future project **after the core live POS is stable**; first document and test a manual recovery plan, not automatic failover.
  - Establish how to run the customer website, .NET POS/API and any IIS-dependent components from the Mac (or an appropriately configured Windows/Linux VM), without using staging as a substitute for production.
  - Assess Cloudflare Tunnel or another secure reverse proxy, independent DNS/traffic routing, HTTPS, internet connectivity, and a controlled manual cutover of the website and POS domains. Do not expose a development server directly to the public internet.
  - Design a separate **production-safe emergency SQL database** with off-host backups and a tested restoration/replication strategy. Never point public ordering to the staging database or copy production customer, order or payment data into staging.
  - Define safe outage operation: prevent split-brain ordering; handle unique order IDs, payment callbacks, refunds, customer data, local receipt printing and order acknowledgement without duplicates.
  - Provide clear activation, health checks, monitoring, customer-facing fallback messaging (including telephone ordering), and recovery/failback steps with reconciliation of emergency orders into production **exactly once**.
  - Test host outage, database outage, shop internet failure, recovery, duplicate prevention and restoration in an isolated environment before any live use.
  - Document operational limitations: the Mac must remain powered on; shop-local POS operation may still work without internet, but public website ordering and externally processed card payments require working connectivity and available providers.
  - **Do not switch production DNS, connect to or modify production databases, or deploy an emergency server publicly without explicit approval.**

### Dish image optimisation

- [ ] **Automatically generate and store two optimised image versions for each POS dish**
  - Let staff upload **one** original photograph through POS Menu Management; accept the existing supported JPEG, PNG and WebP formats (currently up to 5 MB).
  - Automatically produce an approximately **800 × 800 px main image** (target **100–150 KB**) and **400 × 400 px thumbnail** (target **30–70 KB**), preferably as WebP. Preserve aspect ratio and avoid unintended cropping; treat file-size targets as guidance, not hard limits.
  - Store both versions as binary data in SQL Server, associated with the same ProductId. Plan a safe schema change from the existing single-image `ProductImages` record.
  - Serve thumbnails to POS and website menu cards, and main images to larger previews/details. Keep existing API clients and existing dish images working during migration.
  - Replace both versions atomically when a photo is updated; use versioned URLs or equivalent cache invalidation so customers see the new photo without repeated unnecessary downloads.
  - Decide whether to retain original high-resolution uploads separately for future reuse, rather than silently discarding them during conversion.
  - Check image quality, mobile loading performance, database growth, and backup/restore coverage. Test with staging POS and staging website only before proposing a production migration.

### Customer and address management — post-launch (low priority)

- [ ] **Improve customer/address storage and local postcode lookup after POS and website launch**
  - Review existing customer and address tables; prefer dedicated tables within each environment's existing SQL Server database unless a separate database is genuinely necessary.
  - Build a postcode-to-street directory from independently confirmed customer-provided address information, without storing personal customer details in the shared directory.
  - When staff enter a postcode, check locally known streets and suggest them alongside Google Places. A postcode may cover multiple streets or buildings: never silently assume one street is correct.
  - Preserve manual address entry and corrections for missing or inaccurate Google results; save confirmed details for future lookups.
  - Consider secure, verified customer-address reuse across POS and website through the POS API; never expose records solely from an unverified phone-number match.
  - Isolate staging and production databases and data. Never copy live customer, order or payment records to staging.
  - Plan schema migrations and tests on staging first; do not modify production databases without explicit approval.

### Other improvements

_Add new shared tasks here as they arise; place POS-only and Website-only tasks in their respective sections._
