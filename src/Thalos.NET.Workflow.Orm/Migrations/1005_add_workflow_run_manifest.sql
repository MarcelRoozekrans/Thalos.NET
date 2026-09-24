-- Continues the 1000+ range Thalos.NET.Workflow.Orm reserved in 1001_create_workflow_tables.sql, for the same
-- reason documented there: __zaorm_migrations is one global sequence shared by every migration source applied
-- against the same database, and a colliding version number is silently skipped, not rejected.

-- A run's pin: which agent revision and skill version each task node runs, plus host-pinned documents.
-- Written once by StartAsync and never updated. NULL means the run predates pinning or was started without it.
ALTER TABLE workflow_run ADD COLUMN manifest jsonb NULL;
