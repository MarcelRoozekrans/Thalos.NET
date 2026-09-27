-- Continues the 1000+ range Thalos.NET.Workflow.Orm reserved in 1001_create_workflow_tables.sql, for the same
-- reason documented there: __zaorm_migrations is one global sequence shared by every migration source applied
-- against the same database, and a colliding version number is silently skipped, not rejected.

-- 1008: the token usage of the node a completion event closes — input, output, cache read and cache write, plus
-- the model id. NULL for events that ran no agent turn: start, resume, fail, gate and host-action nodes.
ALTER TABLE workflow_run_event ADD COLUMN usage jsonb NULL;
