-- Continues the 1000+ range Thalos.NET.Workflow.Orm reserved in 1001_create_workflow_tables.sql, for the same
-- reason documented there: __zaorm_migrations is one global sequence shared by every migration source applied
-- against the same database, and a colliding version number is silently skipped, not rejected.

-- 1009: who caused an event, as a RunPrincipal. Written only on a Retried event; NULL on every other kind.
ALTER TABLE workflow_run_event ADD COLUMN actor jsonb NULL;
