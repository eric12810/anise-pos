# Website Stripe Checkout release requirements

Website card checkout remains disabled unless `WebsiteCheckout:StripeSessionsEnabled=true` and all Stripe mode settings agree. Keep credentials in protected Winserve settings, never in Git or deployment packages.

| Environment | `ASPNETCORE_ENVIRONMENT` | `WebsiteCheckout:StripeMode` | Secret key | Website return hosts |
| --- | --- | --- | --- | --- |
| Staging | `Staging` | `test` | Stripe test key | `www.staging.loveanise.co.uk` or `staging.loveanise.co.uk` |
| Production | `Production` | `live` | Stripe live key | `www.loveanise.co.uk` or `loveanise.co.uk` |

Each site also needs its own `WebsiteCheckout:StripeWebhookSecret` from the matching Stripe webhook endpoint and `WebsiteCheckout:StripeSuccessUrl` and `WebsiteCheckout:StripeCancelUrl`. Set the webhook URL to `https://pos-staging.loveanise.co.uk/api/webhooks/stripe/website` for staging and `https://pos.loveanise.co.uk/api/webhooks/stripe/website` for production. Subscribe to `checkout.session.completed` and `checkout.session.async_payment_succeeded`. Do not reuse webhook secrets or database connections across environments.

Before enabling checkout, compare the active Winserve environment and database identity using the read-only verification procedure in the workspace instructions. Apply `database/011_website_stripe_reconciliation.sql` to staging only after a recoverable staging backup and schema comparison. This adds two nullable checkout alert fields and a unique Stripe Checkout payment reference index. Review existing duplicate references before creating the index. Production migration needs separate approval and backup; API startup is not a read-only verification tool.

The reconciliation worker checks incomplete checkouts every two minutes while IIS is running. Configure a Winserve scheduled keep-alive for the POS site and verify the application remains active during idle periods. Winserve advertises scheduled tasks in its hosting plans; confirm the task is available in this account before enabling live checkout. Manager accounts see unresolved checkout alerts on the order queue. Review alerts and Stripe payment records before taking a replacement order or issuing a manual refund. Also monitor application logs for reconciliation errors, including failure to write an alert when SQL is unavailable.

Staging acceptance checks: test paid webhook and duplicate webhook, failed and abandoned payments, lost webhook with reconciliation, POS and SQL outage recovery, repeated reconciliation, mismatched session and amount, and failed, pending, retried and completed refunds. Confirm website cash orders and manual POS payments still work. Do not enable production card checkout until these checks pass against staging with Stripe test credentials and the Winserve keep-alive and webhook delivery are observed.
