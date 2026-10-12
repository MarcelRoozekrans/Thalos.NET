-- 0.16.0: IWorkflowRunQuery lists runs newest first by (created_at, id) with keyset paging.
-- Additive and safe in a rolling deploy. Not CONCURRENTLY, so it briefly blocks writes to workflow_run while it builds.
CREATE INDEX ix_workflow_run_created_at_id ON workflow_run (created_at DESC, id DESC);
