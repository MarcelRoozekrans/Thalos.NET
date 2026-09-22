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

-- At most one version of a process may be active at a time. This partial unique index is what enforces that; the
-- application code is not trusted to maintain it on its own.
--
-- CORRECTED. This comment used to say activation was "a single UPDATE ... in one statement, so two versions should
-- never both be active", with the index described as a backstop for some hypothetical future edit. That reasoning
-- was wrong, and the index was not a backstop — it was already failing real writes. A partial unique index is
-- checked per row as an UPDATE walks them, not once at statement end, so the single statement
-- `SET is_active = (version = @version)` transiently held two active rows whenever the walk reached the version
-- being activated before the one being deactivated, and raised 23505. It happened to work while activation only
-- ever followed the insert of a brand-new version, and broke the first time an already-stored version was
-- re-activated: a rollback to an earlier version, or a restart re-syncing an unchanged file after a newer version
-- had activated.
--
-- Activation is therefore no longer one statement. See OrmProcessDefinitionStore.ActivateAsync, which deactivates
-- the other versions first and activates the target second, passing through a state with zero active rows, under
-- a per-process advisory lock taken by LockProcessAsync. The index's job is unchanged and it is still the thing
-- that makes the invariant structural; only the description of how the writer cooperates with it was wrong.
--
-- Editing this comment is safe: ZeroAlloc.ORM's MigrationRunner tracks applied migrations by version number alone
-- in __zaorm_migrations (version, name, applied_at) and never checksums or re-reads an applied file. Note the
-- corollary — editing the SQL of an applied migration would silently not run on existing databases. Only the
-- comment is changed here; the index itself is untouched.
CREATE UNIQUE INDEX ix_process_definition_one_active_per_process
    ON process_definition (process)
    WHERE is_active;
