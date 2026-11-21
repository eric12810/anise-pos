# Caller lifecycle staging rollout

This change is prepared locally. It has not been applied to staging SQL, pushed, deployed, or installed in the running Mac listener.

`database/013_caller_lifecycle.sql` adds nullable `CallId` and `CallStatus` columns to the existing staging `CallerEvents` table, backfills its short-lived rows as ringing, and adds an index for ordered status checks. It contains no destructive table operation. The API requires these columns before it starts; deploying the API before the migration would break caller event handling.

Before applying the migration, follow the database identity, isolation, schema comparison, backup, and restore checks in `AGENTS.md`. Verify the effective staging SQL connection is `anisedb`, compare the deployed `CallerEvents` schema with migration 012, and obtain approval for the reviewed staging migration. Do not apply it to production.

Rollout order after approval: apply migration 013 to verified staging SQL; deploy the POS staging branch; verify authenticated recent/stream endpoints and a ringing call; install and restart the Mac listener; test answered, missed, caller hangup, duplicate, and rapid consecutive calls. Keep production untouched.

The existing backend event store retains events for four hours so a long call can still receive its final status; the POS recent feed exposes only ten minutes for catch-up. Browser cards remain until dismissed. If the adapter omits an answer event, a later completion cannot reliably distinguish an answered call from a missed call and is classified as missed. If it omits all completion signals, the card retains its last observed status.
