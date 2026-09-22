-- Continues the 1000+ range Thalos.NET.Workflow.Orm reserved in 1001_create_workflow_tables.sql, for the same
-- reason documented there: __zaorm_migrations is one global sequence shared by every migration source applied
-- against the same database, and a colliding version number is silently skipped, not rejected.

CREATE TABLE process_definition
(
    process text NOT NULL,
    version integer NOT NULL,
    yaml text NOT NULL,
    is_active boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (process, version)
);

-- Activation is a single UPDATE keyed on the version just upserted (OrmProcessDefinitionStore.UpsertAndActivateAsync
-- sets is_active = (version = @version) for every row of the process in one statement), so two versions of the
-- same process should never both be active. This partial unique index makes that structurally impossible even if
-- a future edit to that statement breaks the invariant — the write fails loudly instead of leaving two active rows.
CREATE UNIQUE INDEX ix_process_definition_one_active_per_process
    ON process_definition (process)
    WHERE is_active;
