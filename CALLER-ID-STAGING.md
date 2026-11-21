# Caller ID stage 2: staging integration

The Mac listener posts `POST https://pos-staging.loveanise.co.uk/api/caller/events` with JSON:

```json
{"eventId":"53bced41-cc51-42ec-a15c-bf56ba1dc605","callerNumber":"07123456789","timestamp":"2026-11-21T12:34:56Z","eventType":"incoming/ringing"}
```

Use `Authorization: Bearer <dedicated random secret>` over HTTPS. Set `CallerId__IngestSecret` in the **staging POS host environment only**, using a random value of at least 32 characters. Do not put it in appsettings, Git, deployment packages or logs. Configure the same value in the Mac's owner-only secret file. The POS returns 202 for a new event, 200 for an already seen event ID, 400 for an invalid or stale payload, 401 for missing or invalid credentials, 403 for HTTP, and 429 at the rate limit. The Mac must retry the same event ID after transient failures.

Authenticated staff clients can open `GET /api/caller/stream` with their existing staff cookie. It is a server-sent event stream; each `data:` frame contains `eventId`, `timestamp`, canonical `callerNumber`, and, when a saved customer matches, `customerId` and `customerName`. The Mac never supplies customer details. No iPad interface is included in stage 2. The stream is separate from order push notifications.

Customer lookup reads `Customers.Phone`, which is stored as free text up to 30 characters. Existing order address lookup also matches `Orders.CustomerPhone`, but caller matching deliberately uses saved customers only; it never creates or edits customer or order rows. UK `+44`, `0044`, and local leading-zero forms compare as local numbers. International prefixes remain international. Withheld and unknown numbers yield no customer match. No addresses or order history are broadcast.

Caller events now use a shared SQL Server table, so separate staging POS workers can deliver the same event to multiple authenticated browsers. The event ID is unique in SQL; SSE sequence IDs let browsers resume across reconnects without duplicating delivered events. Ingest rejects events older than two minutes. The stream omits events older than two minutes, and a background worker removes stored rows after ten minutes. Browser clients do not poll; the POS API checks the small shared table every second while the SSE connection is open.

**Migration required before deploying this revision:** `database/012_caller_events.sql` adds only `dbo.CallerEvents`, with an identity sequence, unique event ID, event and receive timestamps, normalized caller number, and optional matched customer ID/name. It does not change customer, order, payment, or existing notification tables. The table contains short-lived personal data, so limit database access and verify expiry. No production migration or deployment is included. Before applying on staging, independently verify the active staging database identity and separation from production, ensure a restorable staging backup, compare the existing schema, review this script, and run the migration only through the approved staging process. Deploy the API only after the table exists. Rollback requires approval because dropping the table destroys any retained caller events.

## Safe simulation

With the staging secret available only in a local environment variable, send a synthetic number that is not a real customer:

```sh
curl --fail-with-body --proto '=https' --tlsv1.2 \
  -H 'Content-Type: application/json' -H "Authorization: Bearer $ANISE_CALLER_STAGING_SECRET" \
  -d "{\"eventId\":\"$(uuidgen)\",\"callerNumber\":\"Withheld\",\"timestamp\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",\"eventType\":\"incoming/ringing\"}" \
  https://pos-staging.loveanise.co.uk/api/caller/events
```

Observe the stream only from a browser session already signed into staging POS. Never place the secret in browser code. The sample request is a staging-only test and requires the stage 2 backend to be deployed first.

## Staging deployment preparation

1. Review the POS and Mac diffs; run `dotnet run --project tests/AnisePOS.Checks/AnisePOS.Checks.csproj` and `python3 -m unittest discover -s tests -v` in their respective projects.
2. Confirm the staging site URL, the active staging POS settings and database identity using the project's normal deployment preflight. Verify a restorable staging backup, then review and apply `database/012_caller_events.sql` to staging only before deploying this revision. Keep the production host and database untouched.
3. Install a random dedicated secret in the staging POS host environment and the Mac's owner-only secret file. Configure `pos_url` and `pos_secret_file` in the Mac's local `config.json`.
4. Use the existing staging deployment workflow only after separate deployment approval. Verify staging health and existing order notifications, then simulate a withheld event and check the authenticated stream on two signed-in POS clients.
5. Update the installed Mac listener through its existing launchd installation procedure after reviewing the workspace copy. Changes to this repository alone do not update the running service.
