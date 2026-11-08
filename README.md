# Anise POS

## Receipt printing

Open a customer receipt from the order queue or history, then choose **Print** to send one copy. Automatic order printing sends two copies. The browser sends the receipt to the Epson using ePOS-Print over HTTPS. The device must reach the printer and trust its HTTPS certificate. On Android Chrome, allow local network access when requested. Use Check printer connection on a receipt to check connectivity before printing; the check does not confirm print status. The printer is configured at `192.168.1.234:9100` in `ReceiptPrinter`; override it with `ReceiptPrinter__Host` and `ReceiptPrinter__Port` if needed. The machine running the POS service must be able to reach the printer on the local network; a remotely hosted service needs a local print bridge or private network connection. A successful printer response does not necessarily confirm paper was printed. Check the printer before retrying an uncertain result. Receipt text currently uses ASCII, so unsupported characters are replaced with question marks.

## Staff order queue

Open `/orders.html` after signing in, or use the Order queue link in the POS header (opens a separate tab to preserve the current cart).
The queue refreshes every 15 seconds while visible. New includes existing Draft, Placed and Accepted orders.
Staff advance orders to Preparing, Ready and Completed. Completed orders leave the active queue; their records are retained.
These actions do not take payment or change payment status.

The authenticated `GET /api/order-queue` returns active orders with receipt snapshots.
`PUT /api/order-queue/{orderNumber}/status` takes `expectedStatus` and `status`.
Concurrent stale updates return 409; the status and staff-attributed history are committed in one transaction.
No database migration is required.

Run the transition checks with `dotnet run --project tests/AnisePOS.Checks`.

Clean ASP.NET Core API foundation for a shared iPad POS and website ordering system.

## Database

The schema is designed for SQL Server 2019 (compatibility level 150) and is intentionally lean for the 1 GB `anisedb` hosting limit. Run the numbered SQL scripts in `database` in order to set up the database. Before deploying the multiple-category menu update to an existing database, run [006_product_categories.sql](database/006_product_categories.sql); it preserves existing category assignments.

Set the `ConnectionStrings__AniseDatabase` environment variable for each environment. The checked-in connection string is only a placeholder; do not commit hosting credentials.

For the hosted database, create an environment secret using this shape (substitute the password only in the host's secret manager):

```text
Server=mssqluk20.prosql.net;Database=anisedb;User Id=anise-app;Password=YOUR_PASSWORD;Encrypt=True;TrustServerCertificate=False;
```

Before launch, confirm that the hosting provider presents a certificate valid for `mssqluk20.prosql.net`. If it does not, obtain its supported encrypted connection settings from the provider; do not silently weaken encryption in production. Run `database/001_initial_schema.sql` once against the empty `anisedb` database using the `anise-app` login, then start the API with the secret configured. Remote access being enabled makes this possible, but the login should retain only the permissions required by this schema.

The order tables retain product, variant, and modifier names/prices as snapshots. This preserves historical receipts after menu edits. Monetary values use `decimal(10,2)` and no product images or documents are stored in the database.

## Local setup

Restore packages, set a local SQL Server connection string, and run the API. The `/health` endpoint confirms it is running.

## First API endpoints

- `GET /api/menu` — public, available categories, products, variants, and modifiers.
- `POST /api/menu/categories` — create a category.
- `POST /api/menu/products` — create a product with one or more variants.
- `POST /api/menu/modifier-groups` — create a modifier group and its choices.
- `PUT /api/menu/products/{productId}/modifier-groups/{groupId}` — attach a modifier group to a product.
- `POST /api/orders` — staff-only draft order creation; availability and modifier-selection rules are validated and prices are copied into the order receipt.
- `GET /api/orders/{orderNumber}` — staff-only order and status/payment history lookup.

Menu-management endpoints require an authenticated Owner or Manager session.

## Staff login

Run [002_staff_users.sql](database/002_staff_users.sql) once against `anisedb`. Then, while the API is private/local and before any staff account exists, call `POST /api/staff/initial-owner` with an email, display name, and a password of at least 12 characters. It can succeed only once.

Use `POST /api/staff/login` to create a secure, HTTP-only staff session. Owners can add staff with `POST /api/staff`; Managers can manage the menu; Cashiers can sign in but cannot change menu data. Staff write endpoints require their respective role.

For a local browser check, visit `http://127.0.0.1:5000/staff-login.html` and sign in. A successful sign-in message confirms the session cookie was created.

## iPad POS screen

After signing in, open `http://127.0.0.1:5000/pos.html`. The responsive POS screen reads the shared menu, builds orders with valid variants/modifiers, and creates staff-authorised draft orders.

To populate the menu, an Owner or Manager can use `http://127.0.0.1:5000/menu-admin.html`.

### Stripe telephone card entry (in progress)

Set `Stripe__PublishableKey` in the server environment to the Stripe publishable key to load Stripe-hosted card fields for delivery card orders. The card fields are only an initial integration: no card payment is created or charged by the POS yet. Continue taking telephone payments in the Stripe app. Do not enter real card details into these fields until payment processing is implemented and tested. The secret key must never be placed in browser code or committed to the repository.

## Website card checkout (test mode)

The website card option remains disabled unless all test settings are configured: `WebsiteCheckout__StripeSessionsEnabled=true`, `Stripe__SecretKey` with a Stripe test secret, `WebsiteCheckout__StripeWebhookSecret` with the test webhook signing secret, `WebsiteCheckout__StripeSuccessUrl=https://staging.anise.dotnetcloud.co.uk/order-status.html`, and `WebsiteCheckout__StripeCancelUrl=https://staging.anise.dotnetcloud.co.uk/stripe-cancelled.html`. Configure the Stripe test webhook to send `checkout.session.completed` and `checkout.session.async_payment_succeeded` to `https://www.anise.dotnetcloud.co.uk/api/webhooks/stripe/website`. Secrets belong in the host's secret manager. The webhook records a paid order only after validating its signature, session, currency and amount. The status page waits for that confirmation; it does not infer success from the redirect.

Before enabling, verify test checkout, cancel and retry, decline/refund, webhook delivery, customer status and POS receipt end to end. The integration deliberately rejects live Stripe keys and live webhook events.

Declining a paid website order starts its full Stripe refund automatically. The order remains in the queue and cannot be accepted until Stripe confirms the refund; staff can retry decline if confirmation is delayed. Existing partial or unresolved refunds require checking Stripe before completing the decline.
