# Production caller ID release review

The caller integration has been verified on POS staging. No production SQL,
application, or listener change is made by these review files.

## Required evidence before the SQL change

1. Confirm the effective IIS settings for `pos.loveanise.co.uk`, including
   `ASPNETCORE_ENVIRONMENT` and any `ConnectionStrings__AniseDatabase` override.
   Record only the server and database identifiers.
2. Use an authorised production SQL connection to run only the read-only
   metadata queries in
   `database/production/inspect_caller_events.sql`. It must return
   `anisedatabase` and the intended server. Review the full CallerEvents
   column and index results. No customer or order rows are needed.
3. Record non-secret references and times for a restorable production SQL
   backup and a separate restorable application backup. Confirm restore access.
4. Review the exact SQL in `database/production/create_caller_events.sql`.
   It creates one new caller event table and two indexes, with no data copy.
   It refuses an existing CallerEvents object; that result requires a new plan.
   Obtain explicit approval for this production database write before applying it.

## Application and listener release

- The active V2 production workflow accepts a successful V2 staging artifact,
  but its routine schema guard correctly stops releases whose database
  requirements changed. Caller integration changes `Program.cs` and adds
  database scripts 012 and 013. Use a separately reviewed schema release path
  after the production SQL check and migration; do not disable the routine guard.
- Confirm the production `CallerId:IngestSecret` is configured and distinct
  from staging. Test the authenticated production API before changing the Mac
  listener. The installed listener currently sends to staging and its URL
  allowlist is staging only.
- Release the exact successful staging artifact, verify production `/health`
  and protected caller endpoints, then switch the listener to the production
  endpoint in a separate controlled restart. Test ringing, answered, ended,
  missed, and iPad navigation with a real call.

Production event history is short lived. A lost terminal event can leave an
active button until the browser fallback expires it (five minutes for ringing,
four hours for answered). The listener does not persist unsent events across
process restarts.
