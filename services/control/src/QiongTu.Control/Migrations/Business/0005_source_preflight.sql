CREATE TABLE source_preflight_runs (
    source_preflight_run_id TEXT PRIMARY KEY,
    import_session_id TEXT NOT NULL REFERENCES image_import_sessions(import_session_id) ON DELETE RESTRICT,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    source_root_key_snapshot TEXT NOT NULL CHECK(
        length(source_root_key_snapshot) = 64 AND source_root_key_snapshot NOT GLOB '*[^0-9a-f]*'),
    source_locator_manifest_id_snapshot TEXT NOT NULL CHECK(
        length(source_locator_manifest_id_snapshot) > 0 AND
        length(source_locator_manifest_id_snapshot) <= 128 AND
        instr(source_locator_manifest_id_snapshot, '/') = 0 AND
        instr(source_locator_manifest_id_snapshot, '\') = 0 AND
        instr(source_locator_manifest_id_snapshot, ':') = 0 AND
        instr(source_locator_manifest_id_snapshot, '..') = 0 AND
        source_locator_manifest_id_snapshot <> '.'),
    parser_profile TEXT NOT NULL CHECK(length(parser_profile) > 0 AND length(parser_profile) <= 128),
    parser_version TEXT NOT NULL CHECK(length(parser_version) > 0 AND length(parser_version) <= 128),
    policy_version TEXT NOT NULL CHECK(length(policy_version) > 0 AND length(policy_version) <= 128),
    status TEXT NOT NULL CHECK(status IN ('queued', 'running', 'completed', 'failed', 'interrupted')),
    decision TEXT NULL CHECK(decision IN ('dji_supported', 'out_of_scope', 'unconfirmed')),
    decision_reason_code TEXT NULL CHECK(
        decision_reason_code IS NULL OR
        (length(decision_reason_code) > 0 AND length(decision_reason_code) <= 128)),
    total_item_count INTEGER NOT NULL DEFAULT 0 CHECK(total_item_count >= 0),
    image_candidate_count INTEGER NOT NULL DEFAULT 0 CHECK(image_candidate_count >= 0),
    sidecar_candidate_count INTEGER NOT NULL DEFAULT 0 CHECK(sidecar_candidate_count >= 0),
    completed_item_count INTEGER NOT NULL DEFAULT 0 CHECK(completed_item_count >= 0),
    supports_dji_item_count INTEGER NOT NULL DEFAULT 0 CHECK(supports_dji_item_count >= 0),
    out_of_scope_item_count INTEGER NOT NULL DEFAULT 0 CHECK(out_of_scope_item_count >= 0),
    unconfirmed_item_count INTEGER NOT NULL DEFAULT 0 CHECK(unconfirmed_item_count >= 0),
    conflict_item_count INTEGER NOT NULL DEFAULT 0 CHECK(conflict_item_count >= 0),
    failed_item_count INTEGER NOT NULL DEFAULT 0 CHECK(failed_item_count >= 0),
    blocking_image_count INTEGER NOT NULL DEFAULT 0 CHECK(blocking_image_count >= 0),
    evidence_summary_json TEXT NULL CHECK(
        evidence_summary_json IS NULL OR
        (length(evidence_summary_json) > 0 AND length(evidence_summary_json) <= 16384 AND json_valid(evidence_summary_json))),
    failure_code TEXT NULL CHECK(
        failure_code IS NULL OR (length(failure_code) > 0 AND length(failure_code) <= 128)),
    created_at_utc TEXT NOT NULL,
    started_at_utc TEXT NULL,
    updated_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    UNIQUE(import_session_id),
    CHECK(total_item_count = image_candidate_count + sidecar_candidate_count),
    CHECK(completed_item_count <= total_item_count),
    CHECK(blocking_image_count <= image_candidate_count),
    CHECK(supports_dji_item_count + out_of_scope_item_count + unconfirmed_item_count +
          conflict_item_count + failed_item_count = completed_item_count),
    CHECK(status <> 'running' OR started_at_utc IS NOT NULL),
    CHECK(status <> 'completed' OR
          (decision IS NOT NULL AND decision_reason_code IS NOT NULL AND
           evidence_summary_json IS NOT NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status NOT IN ('failed', 'interrupted') OR
          (decision IS NULL AND failure_code IS NOT NULL)),
    CHECK(status IN ('queued', 'running') OR completed_at_utc IS NOT NULL),
    CHECK(decision <> 'dji_supported' OR
          (supports_dji_item_count > 0 AND blocking_image_count = 0))
);

CREATE TABLE source_preflight_items (
    source_preflight_item_id TEXT PRIMARY KEY,
    source_preflight_run_id TEXT NOT NULL REFERENCES source_preflight_runs(source_preflight_run_id) ON DELETE RESTRICT,
    import_session_id TEXT NOT NULL REFERENCES image_import_sessions(import_session_id) ON DELETE RESTRICT,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    import_entry_id TEXT NULL REFERENCES image_import_entries(import_entry_id) ON DELETE RESTRICT,
    source_entry_key TEXT NOT NULL CHECK(
        length(source_entry_key) = 64 AND source_entry_key NOT GLOB '*[^0-9a-f]*'),
    display_name TEXT NOT NULL CHECK(
        length(display_name) > 0 AND length(display_name) <= 255 AND
        instr(display_name, '/') = 0 AND instr(display_name, '\') = 0 AND instr(display_name, ':') = 0),
    sort_index INTEGER NOT NULL CHECK(sort_index >= 0),
    candidate_kind TEXT NOT NULL CHECK(candidate_kind IN ('image_candidate', 'positioning_aux_candidate')),
    format_hint TEXT NULL CHECK(
        format_hint IN ('jpg', 'jpeg', 'mpo', 'tif', 'tiff', 'mrk', 'nav', 'obs', 'rtk')),
    byte_length_snapshot INTEGER NULL CHECK(byte_length_snapshot IS NULL OR byte_length_snapshot >= 0),
    source_last_write_time_utc TEXT NULL,
    source_identity_key TEXT NULL CHECK(
        source_identity_key IS NULL OR
        (length(source_identity_key) = 64 AND source_identity_key NOT GLOB '*[^0-9a-f]*')),
    status TEXT NOT NULL CHECK(status IN ('queued', 'running', 'completed', 'failed')),
    container_hint TEXT NULL CHECK(
        container_hint IN ('jpeg_hint', 'mpo_hint', 'tiff', 'bigtiff', 'not_image', 'unknown')),
    evidence_state TEXT NULL CHECK(
        evidence_state IN ('supports_dji', 'out_of_scope', 'unconfirmed', 'conflict', 'read_failed')),
    evidence_json TEXT NULL CHECK(
        evidence_json IS NULL OR
        (length(evidence_json) > 0 AND length(evidence_json) <= 8192 AND json_valid(evidence_json))),
    parser_profile TEXT NULL CHECK(
        parser_profile IS NULL OR (length(parser_profile) > 0 AND length(parser_profile) <= 128)),
    parser_version TEXT NULL CHECK(
        parser_version IS NULL OR (length(parser_version) > 0 AND length(parser_version) <= 128)),
    failure_code TEXT NULL CHECK(
        failure_code IS NULL OR (length(failure_code) > 0 AND length(failure_code) <= 128)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    UNIQUE(source_preflight_run_id, source_entry_key),
    UNIQUE(source_preflight_run_id, sort_index),
    CHECK(candidate_kind <> 'image_candidate' OR import_entry_id IS NOT NULL),
    CHECK(candidate_kind <> 'positioning_aux_candidate' OR import_entry_id IS NULL),
    CHECK(status <> 'completed' OR
          (container_hint IS NOT NULL AND evidence_state IS NOT NULL AND
           evidence_json IS NOT NULL AND parser_profile IS NOT NULL AND
           parser_version IS NOT NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status <> 'failed' OR (failure_code IS NOT NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status IN ('queued', 'running') OR completed_at_utc IS NOT NULL)
);

CREATE INDEX idx_source_preflight_runs_dataset
ON source_preflight_runs(dataset_version_id, created_at_utc DESC, source_preflight_run_id DESC);

CREATE INDEX idx_source_preflight_items_run
ON source_preflight_items(source_preflight_run_id, sort_index, source_preflight_item_id);

CREATE INDEX idx_source_preflight_items_import_entry
ON source_preflight_items(import_entry_id)
WHERE import_entry_id IS NOT NULL;

ALTER TABLE dataset_versions
ADD COLUMN source_eligibility_run_id TEXT NULL
REFERENCES source_preflight_runs(source_preflight_run_id) ON DELETE RESTRICT;

ALTER TABLE dataset_versions
ADD COLUMN source_eligibility_decided_at_utc TEXT NULL;

CREATE TRIGGER validate_source_preflight_run_insert
BEFORE INSERT ON source_preflight_runs
WHEN NOT EXISTS (
    SELECT 1
    FROM image_import_sessions s
    JOIN dataset_versions dv ON dv.dataset_version_id = s.dataset_version_id
    WHERE s.import_session_id = NEW.import_session_id
      AND s.dataset_version_id = NEW.dataset_version_id
      AND s.source_root_key = NEW.source_root_key_snapshot
      AND s.source_locator_manifest_id = NEW.source_locator_manifest_id_snapshot
      AND s.status = 'awaiting_source_preflight'
      AND dv.lifecycle_state = 'draft'
      AND dv.source_eligibility_state IN ('pending', 'unconfirmed'))
BEGIN
    SELECT RAISE(ABORT, 'source preflight requires a stable waiting import session and draft dataset version');
END;

CREATE TRIGGER validate_source_preflight_run_update
BEFORE UPDATE ON source_preflight_runs
WHEN NEW.import_session_id IS NOT OLD.import_session_id
   OR NEW.dataset_version_id IS NOT OLD.dataset_version_id
   OR NEW.source_root_key_snapshot IS NOT OLD.source_root_key_snapshot
   OR NEW.source_locator_manifest_id_snapshot IS NOT OLD.source_locator_manifest_id_snapshot
   OR NEW.parser_profile IS NOT OLD.parser_profile
   OR NEW.parser_version IS NOT OLD.parser_version
   OR NEW.policy_version IS NOT OLD.policy_version
   OR NOT EXISTS (
        SELECT 1
        FROM image_import_sessions s
        JOIN dataset_versions dv ON dv.dataset_version_id = s.dataset_version_id
        WHERE s.import_session_id = NEW.import_session_id
          AND s.dataset_version_id = NEW.dataset_version_id
          AND s.source_root_key = NEW.source_root_key_snapshot
          AND s.source_locator_manifest_id = NEW.source_locator_manifest_id_snapshot
          AND dv.lifecycle_state = 'draft')
BEGIN
    SELECT RAISE(ABORT, 'source preflight identity and draft dataset binding are immutable');
END;

CREATE TRIGGER validate_source_preflight_run_transition
BEFORE UPDATE OF status ON source_preflight_runs
WHEN NEW.status IS NOT OLD.status AND NOT (
    (OLD.status = 'queued' AND NEW.status IN ('running', 'failed', 'interrupted')) OR
    (OLD.status = 'running' AND NEW.status IN ('completed', 'failed', 'interrupted')) OR
    (OLD.status = 'interrupted' AND NEW.status IN ('running', 'failed')))
BEGIN
    SELECT RAISE(ABORT, 'invalid source preflight run transition');
END;

CREATE TRIGGER immutable_terminal_source_preflight_run
BEFORE UPDATE ON source_preflight_runs
WHEN OLD.status IN ('completed', 'failed')
BEGIN
    SELECT RAISE(ABORT, 'terminal source preflight run is immutable');
END;

CREATE TRIGGER validate_source_preflight_item_insert
BEFORE INSERT ON source_preflight_items
WHEN NOT EXISTS (
    SELECT 1
    FROM source_preflight_runs r
    JOIN image_import_sessions s ON s.import_session_id = r.import_session_id
    JOIN dataset_versions dv ON dv.dataset_version_id = r.dataset_version_id
    WHERE r.source_preflight_run_id = NEW.source_preflight_run_id
      AND r.import_session_id = NEW.import_session_id
      AND r.dataset_version_id = NEW.dataset_version_id
      AND r.status IN ('queued', 'running')
      AND s.status = 'awaiting_source_preflight'
      AND dv.lifecycle_state = 'draft')
   OR (NEW.candidate_kind = 'image_candidate' AND NOT EXISTS (
        SELECT 1
        FROM image_import_entries e
        WHERE e.import_entry_id = NEW.import_entry_id
          AND e.import_session_id = NEW.import_session_id
          AND e.dataset_version_id = NEW.dataset_version_id
          AND e.source_entry_key = NEW.source_entry_key
          AND e.status = 'awaiting_source_preflight'))
BEGIN
    SELECT RAISE(ABORT, 'source preflight item must match its waiting run and source candidate');
END;

CREATE TRIGGER validate_source_preflight_item_update
BEFORE UPDATE ON source_preflight_items
WHEN NEW.source_preflight_run_id IS NOT OLD.source_preflight_run_id
   OR NEW.import_session_id IS NOT OLD.import_session_id
   OR NEW.dataset_version_id IS NOT OLD.dataset_version_id
   OR NEW.import_entry_id IS NOT OLD.import_entry_id
   OR NEW.source_entry_key IS NOT OLD.source_entry_key
   OR NEW.display_name IS NOT OLD.display_name
   OR NEW.sort_index IS NOT OLD.sort_index
   OR NEW.candidate_kind IS NOT OLD.candidate_kind
   OR NEW.format_hint IS NOT OLD.format_hint
   OR NEW.byte_length_snapshot IS NOT OLD.byte_length_snapshot
   OR NEW.source_last_write_time_utc IS NOT OLD.source_last_write_time_utc
   OR NEW.source_identity_key IS NOT OLD.source_identity_key
   OR NOT EXISTS (
        SELECT 1 FROM source_preflight_runs r
        WHERE r.source_preflight_run_id = NEW.source_preflight_run_id
          AND r.status = 'running')
BEGIN
    SELECT RAISE(ABORT, 'source preflight item identity is immutable and requires a running preflight');
END;

CREATE TRIGGER immutable_terminal_source_preflight_item
BEFORE UPDATE ON source_preflight_items
WHEN OLD.status IN ('completed', 'failed')
BEGIN
    SELECT RAISE(ABORT, 'terminal source preflight item is immutable');
END;

CREATE TRIGGER immutable_source_preflight_item_delete
BEFORE DELETE ON source_preflight_items
BEGIN
    SELECT RAISE(ABORT, 'source preflight items are immutable audit records');
END;

CREATE TRIGGER validate_dataset_source_eligibility_run_update
BEFORE UPDATE OF source_eligibility_run_id ON dataset_versions
WHEN NEW.source_eligibility_run_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM source_preflight_runs r
    WHERE r.source_preflight_run_id = NEW.source_eligibility_run_id
      AND r.dataset_version_id = NEW.dataset_version_id
      AND r.status = 'completed'
      AND r.decision = NEW.source_eligibility_state
      AND NEW.source_eligibility_decided_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'dataset source eligibility must reference its completed preflight decision');
END;
