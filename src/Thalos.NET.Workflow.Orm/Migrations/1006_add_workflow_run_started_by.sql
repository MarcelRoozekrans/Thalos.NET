-- Continues the 1000+ range reserved in 1001. Who started the run: principal id, roles and display name as
-- the host saw them at start. Written once by StartAsync, never updated. NULL only for runs started before 0.11.0;
-- from 0.11.0 every start records one.
ALTER TABLE workflow_run ADD COLUMN started_by jsonb NULL;
