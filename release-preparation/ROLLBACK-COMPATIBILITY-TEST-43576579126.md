# Original POS binary compatibility test — 14 November 2026

## Isolation and input

- Application input: all 122 managed files checked against the original production manifest SHA-256 `064bcd606d6160edfb1bbba3b63d4f545783eac9c32be5e8d522243952f91efd`. The 64 rollback files came from the local `liveposftp` copy. Four unchanged dependency DLLs came from local staging artifacts only after their bytes matched the same original manifest.
- The test package excluded production `web.config` and production `appsettings.json`. It used a minimal local configuration, loopback HTTP, a loopback SQL connection, disabled Stripe sessions and disabled printing. No production credentials, customer records, orders, payments or data-protection keys were copied.
- Database: disposable local SQL Server 2022 container, database `AniseRollbackSynthetic`, compatibility level 150. Applied base schema 001/002/004-009, the legacy prerequisite schema, then migrations 010 and 011. Migration 003 was not run because it is a historical menu import. Added the older application's mapped `Products.VeganSection` column after the numbered and legacy scripts omitted it; this correction was confined to the synthetic database.

## Results

| Check | Result |
| --- | --- |
| Old binary starts | Pass |
| `GET /health` | HTTP 200, database OK |
| Synthetic staff owner creation, login and authenticated identity | Pass |
| Synthetic category/product creation and public menu load | Pass |
| Synthetic order creation | Pass |
| Synthetic cash payment marked paid; SQL row persisted | Pass |
| Migration 010/011 columns, Refunds table and payment index | Present |
| Schema metadata before/after clean startup at compatibility level 150 | Identical SHA-256 `ac43565fc136c1fea35eceabd602cf7fa6d4b1be148aab3116d10e9f0a00f602` |

An initial menu write failed because the synthetic fixture omitted `Products.VeganSection`; the isolated fixture was corrected and the complete checks then passed. That failure did not establish a production defect. The test did not exercise Stripe, printer, email, push notifications, real payments, production SQL Server 2019 engine behavior, or Winserve IIS hosting. The production rollback still requires fresh production file/configuration checks and separate explicit approval.
