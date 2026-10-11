-- Continues the 1000+ range reserved in 1001. The W3C traceparent that was current when StartAsync wrote the run, so
-- each node dispatch can link its span back to the request that started the run. Written once by StartAsync, never
-- updated. NULL for a run started without an active Activity, and for every run started before 0.15.0.
ALTER TABLE workflow_run ADD COLUMN trace_parent text NULL;
