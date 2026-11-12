# Anise POS — To-do List

Track planned work for the Anise POS and connected Anise website. Tick tasks off as they are completed.

## Telephone orders and payments

- [ ] **Add “Copy & Open Stripe” button**
  - Keep the existing **Copy Total** button unchanged.
  - Copy the exact payable order total to the iPad clipboard.
  - Open `https://dashboard.stripe.com/` in a new Safari tab or window without replacing the current POS order.
  - Staff can use Safari's **Open in Stripe app** banner, where available; a direct app-opening link has not been verified.
  - Preserve the order when staff return to the POS.
  - **Never automatically mark an order paid** just because Stripe has opened. Staff should confirm the successful payment in Stripe before manually recording it in POS.
  - Test on the shop iPad: clipboard, Safari pop-up behaviour, app banner, returning to POS, cancelled payments and failed payments.

### Implementation notes

- This is a convenience shortcut for the existing telephone-payment workflow, not a Stripe MOTO API integration.
- Do not collect, store or log customers' card details in Anise POS.

---

## Deployment safety and recovery

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

---

## Dish image optimisation

- [ ] **Automatically generate and store two optimised image versions for each POS dish**
  - Let staff upload **one** original photograph through POS Menu Management; accept the existing supported JPEG, PNG and WebP formats (currently up to 5 MB).
  - Automatically produce an approximately **800 × 800 px main image** (target **100–150 KB**) and **400 × 400 px thumbnail** (target **30–70 KB**), preferably as WebP. Preserve aspect ratio and avoid unintended cropping; treat file-size targets as guidance, not hard limits.
  - Store both versions as binary data in SQL Server, associated with the same ProductId. Plan a safe schema change from the existing single-image `ProductImages` record.
  - Serve thumbnails to POS and website menu cards, and main images to larger previews/details. Keep existing API clients and existing dish images working during migration.
  - Replace both versions atomically when a photo is updated; use versioned URLs or equivalent cache invalidation so customers see the new photo without repeated unnecessary downloads.
  - Decide whether to retain original high-resolution uploads separately for future reuse, rather than silently discarding them during conversion.
  - Check image quality, mobile loading performance, database growth, and backup/restore coverage. Test with staging POS and staging website only before proposing a production migration.

---

## End-of-day reporting and bookkeeping

- [ ] **Persist end-of-day platform takings and daily closing reports in SQL Server**
  - Replace the current printout-only `sessionStorage` workflow with a server-side saved closing record for each **trading date**. Keep the existing end-of-day preview and print format working.
  - Save the manually entered **gross takings** separately for **Just Eat, Uber Eats, Deliveroo and Other platforms**, alongside a snapshot of the Anise POS order/payment totals used for the closing report. Do not treat net platform payouts as gross sales.
  - Allow authorised staff to retrieve previous closing records by date, view the stored platform breakdown, and **reprint** the report without retyping figures. Add **CSV export** for bookkeeping and future accounting integration.
  - Allow corrections without silently overwriting history: record original and revised values, editor, timestamp and optional reason; use appropriate Owner/Manager permissions and preserve an audit trail.
  - Prevent double-counting: distinguish orders already recorded in POS from externally entered platform totals before calculating **combined takings**. Clearly label POS sales, external sales and totals, and don't add the same order twice.
  - Decide how repeat saves, incomplete days, late adjustments and missing/zero entries behave. Use date-unique closing records or explicit revisions rather than accidental duplicate daily records.
  - Prepare a **staging-only** database migration and tests for saving, refreshing, retrieving, editing, printing, exporting and avoiding double counts. Keep staging and production databases separate; **do not migrate or modify production without explicit approval**.

---

## Other improvements

_Add new tasks here as they arise._
