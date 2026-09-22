-- Continues the 1000+ range Thalos.NET.Workflow.Orm reserved in 1001_create_workflow_tables.sql, for the same
-- reason documented there: __zaorm_migrations is one global sequence shared by every migration source applied
-- against the same database, and a colliding version number is silently skipped, not rejected.

-- OrmWorkflowStore.FindStrandedAsync filters on status = 'Running' and orders by updated_at ascending. Without
-- this index that query is a sequential scan plus a top-N sort over workflow_run, a table that accumulates
-- every run ever started — including every terminal one, which this query never matches. The partial index
-- covers exactly the rows the query can return, so the WHERE clause and the ORDER BY both resolve to a single
-- index scan instead: a stranded-run sweep stays cheap no matter how large the run history grows.
CREATE INDEX ix_workflow_run_updated_at_running
    ON workflow_run (updated_at)
    WHERE status = 'Running';
