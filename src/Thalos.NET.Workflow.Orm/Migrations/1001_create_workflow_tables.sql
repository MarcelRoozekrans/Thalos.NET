-- Version numbers in ZeroAlloc.ORM's MigrationRunner history table (__zaorm_migrations) are a single global
-- sequence shared by every migration source applied against the same database — the history table's primary
-- key is "version" alone, with no per-source namespacing. ZeroAlloc.Outbox.Orm's own schema (outboxmessages)
-- claims version 1. Thalos.NET.Workflow.Orm reserves the 1000+ range so a consumer who also runs the outbox
-- migration against the same database never collides: a colliding version number is not rejected, it is
-- silently skipped as "already applied", and the colliding table is never created.

CREATE TABLE workflow_run
(
    id uuid PRIMARY KEY,
    process text NOT NULL,
    process_version integer NOT NULL,
    correlation_key text NOT NULL,
    current_node text NOT NULL,
    current_seq bigint NOT NULL,
    status text NOT NULL,
    awaiting_signal text NULL,
    visits jsonb NOT NULL,
    last_error text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- StartAsync is keyed for idempotent lookup by correlation_key: a second StartAsync call for a
-- correlation_key already in flight returns the existing run instead of creating a duplicate.
CREATE UNIQUE INDEX ix_workflow_run_correlation_key ON workflow_run (correlation_key);

-- Append-only: no UPDATE statement against this table is ever written anywhere in
-- Thalos.Workflow.Orm — OrmWorkflowStore has no method that issues one — so append-only is a
-- structural property of the code, not a convention someone could accidentally violate by adding
-- one more SQL string. workflow_run is the only row that ever changes; this table only grows.
CREATE TABLE workflow_run_event
(
    id bigserial PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES workflow_run (id),
    seq bigint NOT NULL,
    kind text NOT NULL,
    from_node text NULL,
    to_node text NOT NULL,
    status text NOT NULL,
    awaiting_signal text NULL,
    outcome text NULL,
    variables jsonb NULL,
    error text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_workflow_run_event_run_id ON workflow_run_event (run_id, seq);
