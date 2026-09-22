-- Continues the 1000+ range Thalos.NET.Workflow.Orm reserved in 1001_create_workflow_tables.sql, for the same
-- reason documented there: __zaorm_migrations is one global sequence shared by every migration source applied
-- against the same database, and a colliding version number is silently skipped, not rejected.

-- Makes "a pinned version is immutable" an enforced invariant rather than a convention the sync discipline
-- keeps. Before this column, UpsertAndActivateAsync's ON CONFLICT DO UPDATE SET yaml rewrote a version's
-- definition in place, so a run pinned to version 3 could have the graph it started on changed underneath it.
-- An in-process cache can evict on its own writes, but a second host's cache cannot see them — two hosts could
-- execute different graphs for the same pinned version. With the hash stored, a re-sync of the same version
-- with different content is refused outright, so the version number is a real promise about the content.
ALTER TABLE process_definition ADD COLUMN content_hash text NOT NULL DEFAULT '';

-- Backfills rows written before this column existed. Must produce byte-for-byte what the application computes
-- for the same YAML, or the first re-sync of an unchanged file would read as a content change and be refused:
-- lowercase hex of SHA-256 over the UTF-8 bytes, matching Convert.ToHexStringLower of SHA256.HashData of
-- Encoding.UTF8.GetBytes in OrmProcessDefinitionStore.ComputeContentHash.
UPDATE process_definition SET content_hash = encode(sha256(convert_to(yaml, 'UTF8')), 'hex');

-- The default existed only to let the ALTER add a NOT NULL column to a populated table. Dropped now that every
-- row is backfilled: an INSERT that forgets to supply a hash must fail loudly rather than store an empty one,
-- which would compare unequal to every real hash and make that version permanently unsyncable.
ALTER TABLE process_definition ALTER COLUMN content_hash DROP DEFAULT;
