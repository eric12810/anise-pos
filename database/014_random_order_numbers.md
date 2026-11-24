# 014 — random order numbers

This script is staging-only (`anisedb`). It has not been run. Stop all order-writing API instances and background workers for the cutover: old code cannot insert into the converted schema, and new code cannot insert explicit numbers into the identity column. There is no compatible mixed-version window.

## Read-only preflight

Verify active Winserve/IIS environment overrides and site root, then use an authorised read-only SQL session to confirm `SELECT DB_NAME() AS DatabaseName, @@SERVERNAME AS ServerName;`. Confirm the staging database is distinct from production. Verify a restorable staging SQL Server backup. Compare the actual `dbo.Orders` columns, indexes, constraints, triggers and dependent objects with the script's assumptions. In particular run:

```sql
SELECT COUNT_BIG(*) AS OrderCount, COUNT_BIG(DISTINCT OrderNumber) AS DistinctNumbers,
       MIN(OrderNumber) AS MinimumNumber, MAX(OrderNumber) AS MaximumNumber
FROM dbo.Orders;
SELECT name, is_identity FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Orders') AND name=N'OrderNumber';
SELECT name, type_desc FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Orders');
SELECT name, type_desc FROM sys.objects WHERE parent_object_id=OBJECT_ID(N'dbo.Orders') AND type IN ('UQ','F','TR','D','C');
```

Check the SQL migration history and confirm 014 has not run. Stop if unexpected dependencies or schema differences appear. The migration itself checks the database name, identity state, unique constraint and unexpected index or foreign key dependencies.

## Cutover and verification

With writers stopped and the backup verified, apply `014_random_order_numbers.sql` to staging in one SQL session. Its transaction copies each number into a new bigint column, validates the copy, drops the old identity column, renames the replacement, recreates uniqueness and checks row count. Any SQL error rolls back the transaction. Verify row count, distinct numbers, GUID relationships, non-identity state and unique constraint afterward. Deploy the matching API build to staging, then resume writers. Test a POS order, website cash order and Stripe test-mode checkout using synthetic data; repeat each checkout reference to verify the original order is returned. Check receipt, queue, history, status, refund and notification views, including historical four-digit numbers. Confirm the staging website calls staging POS.

## Recovery

Before commit, SQL rollback restores the old schema automatically. If the migration fails, keep writers stopped and inspect the failure; do not start either API version against an uncertain schema. After commit, a simple down script would reintroduce sequential identity semantics and risk changing numbers. Recover using the verified staging SQL backup, with explicit approval for the destructive restore, then redeploy the old API build. Reconcile any orders accepted after the backup before restoring. Production needs its own identity and backup verification and a separately reviewed migration guard before any production execution.
