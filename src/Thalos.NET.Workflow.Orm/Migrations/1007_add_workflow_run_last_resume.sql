-- Who last resumed the run through a gate, and when. Written only by ResumeAsync, in the same transaction as the
-- resume's own transition. NULL = never resumed; a run with a resume has passed a human gate.
ALTER TABLE workflow_run ADD COLUMN last_resumed_by jsonb NULL, ADD COLUMN last_resumed_at timestamptz NULL;
