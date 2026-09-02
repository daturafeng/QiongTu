CREATE TABLE positioning_aux_upgrade_guard (
    existing_record_count INTEGER NOT NULL CHECK(existing_record_count = 0)
);

INSERT INTO positioning_aux_upgrade_guard(existing_record_count)
SELECT
    (SELECT count(*) FROM positioning_aux_files) +
    (SELECT count(*) FROM positioning_aux_usage);

DROP TABLE positioning_aux_upgrade_guard;

DROP TRIGGER immutable_sealed_positioning_insert;
DROP TRIGGER immutable_sealed_positioning_update;
DROP TRIGGER immutable_sealed_positioning_delete;
DROP TRIGGER validate_positioning_usage_execution;
DROP TRIGGER immutable_positioning_usage_update;
DROP TRIGGER immutable_positioning_usage_delete;

DROP TABLE positioning_aux_usage;
DROP TABLE positioning_aux_files;

CREATE TABLE positioning_aux_import_runs (
    positioning_aux_import_run_id TEXT PRIMARY KEY,
    import_session_id TEXT NOT NULL REFERENCES image_import_sessions(import_session_id) ON DELETE RESTRICT,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    source_preflight_run_id TEXT NOT NULL REFERENCES source_preflight_runs(source_preflight_run_id) ON DELETE RESTRICT,
    association_policy_version TEXT NOT NULL CHECK(length(association_policy_version) > 0 AND length(association_policy_version) <= 128),
    parser_profile TEXT NOT NULL CHECK(length(parser_profile) > 0 AND length(parser_profile) <= 128),
    parser_version TEXT NOT NULL CHECK(length(parser_version) > 0 AND length(parser_version) <= 128),
    status TEXT NOT NULL CHECK(status IN ('pending', 'running', 'completed', 'blocked', 'interrupted', 'cancelled')),
    total_item_count INTEGER NOT NULL DEFAULT 0 CHECK(total_item_count >= 0),
    completed_item_count INTEGER NOT NULL DEFAULT 0 CHECK(completed_item_count >= 0),
    failed_item_count INTEGER NOT NULL DEFAULT 0 CHECK(failed_item_count >= 0),
    last_error_code TEXT NULL CHECK(last_error_code IS NULL OR (length(last_error_code) > 0 AND length(last_error_code) <= 128)),
    created_at_utc TEXT NOT NULL,
    started_at_utc TEXT NULL,
    updated_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    cancelled_at_utc TEXT NULL,
    CHECK(completed_item_count + failed_item_count <= total_item_count),
    CHECK(status <> 'running' OR started_at_utc IS NOT NULL),
    CHECK(status <> 'completed' OR (
        total_item_count = completed_item_count + failed_item_count AND
        last_error_code IS NULL AND completed_at_utc IS NOT NULL AND cancelled_at_utc IS NULL)),
    CHECK(status <> 'blocked' OR (last_error_code IS NOT NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status <> 'cancelled' OR (last_error_code IS NOT NULL AND cancelled_at_utc IS NOT NULL)),
    CHECK(status NOT IN ('pending', 'running', 'interrupted') OR completed_at_utc IS NULL),
    UNIQUE(import_session_id, association_policy_version, parser_profile, parser_version)
);

CREATE TABLE positioning_aux_import_items (
    positioning_aux_import_item_id TEXT PRIMARY KEY,
    positioning_aux_import_run_id TEXT NOT NULL REFERENCES positioning_aux_import_runs(positioning_aux_import_run_id) ON DELETE RESTRICT,
    source_preflight_item_id TEXT NOT NULL UNIQUE REFERENCES source_preflight_items(source_preflight_item_id) ON DELETE RESTRICT,
    source_entry_key TEXT NOT NULL CHECK(length(source_entry_key) = 64 AND source_entry_key NOT GLOB '*[^0-9a-f]*'),
    display_name TEXT NOT NULL CHECK(
        length(display_name) > 0 AND length(display_name) <= 255 AND
        instr(display_name, '/') = 0 AND instr(display_name, '\') = 0 AND instr(display_name, ':') = 0),
    sort_index INTEGER NOT NULL CHECK(sort_index >= 0),
    auxiliary_type TEXT NOT NULL CHECK(auxiliary_type IN ('mrk', 'nav', 'obs', 'rtk')),
    byte_length_snapshot INTEGER NOT NULL CHECK(byte_length_snapshot >= 0),
    source_last_write_time_utc TEXT NULL,
    source_identity_key TEXT NOT NULL CHECK(length(source_identity_key) = 64 AND source_identity_key NOT GLOB '*[^0-9a-f]*'),
    association_item_count INTEGER NOT NULL CHECK(association_item_count > 0),
    status TEXT NOT NULL CHECK(status IN (
        'pending', 'staging', 'staged', 'publishing', 'retained', 'parsing',
        'completed', 'blocked', 'interrupted')),
    stage_id TEXT NULL CHECK(stage_id IS NULL OR (length(stage_id) > 0 AND length(stage_id) <= 128)),
    stage_sha256 TEXT NULL CHECK(stage_sha256 IS NULL OR (length(stage_sha256) = 64 AND stage_sha256 NOT GLOB '*[^0-9a-f]*')),
    stage_byte_length INTEGER NULL CHECK(stage_byte_length IS NULL OR stage_byte_length >= 0),
    stage_created_at_utc TEXT NULL,
    expected_content_hash TEXT NULL CHECK(expected_content_hash IS NULL OR (length(expected_content_hash) = 64 AND expected_content_hash NOT GLOB '*[^0-9a-f]*')),
    expected_byte_length INTEGER NULL CHECK(expected_byte_length IS NULL OR expected_byte_length >= 0),
    expected_object_key TEXT NULL CHECK(expected_object_key IS NULL OR expected_object_key = 'sha256/' || substr(expected_content_hash, 1, 2) || '/' || expected_content_hash),
    file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    positioning_aux_file_id TEXT NULL UNIQUE REFERENCES positioning_aux_files(positioning_aux_file_id) ON DELETE RESTRICT,
    failure_code TEXT NULL CHECK(failure_code IS NULL OR (length(failure_code) > 0 AND length(failure_code) <= 128)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    terminal_at_utc TEXT NULL,
    UNIQUE(positioning_aux_import_run_id, source_entry_key),
    UNIQUE(positioning_aux_import_run_id, sort_index),
    CHECK(status NOT IN ('staged', 'publishing', 'retained', 'parsing', 'completed') OR
          (stage_id IS NOT NULL AND stage_sha256 IS NOT NULL AND stage_byte_length IS NOT NULL AND stage_created_at_utc IS NOT NULL)),
    CHECK(status NOT IN ('publishing', 'retained', 'parsing', 'completed') OR
          (expected_content_hash IS NOT NULL AND expected_byte_length IS NOT NULL AND expected_object_key IS NOT NULL)),
    CHECK(status NOT IN ('publishing', 'retained', 'parsing', 'completed') OR
          (stage_sha256 = expected_content_hash AND stage_byte_length = expected_byte_length)),
    CHECK(status NOT IN ('retained', 'parsing', 'completed') OR
          (file_object_id IS NOT NULL AND positioning_aux_file_id IS NOT NULL)),
    CHECK(status <> 'completed' OR (failure_code IS NULL AND terminal_at_utc IS NOT NULL)),
    CHECK(status <> 'blocked' OR (failure_code IS NOT NULL AND terminal_at_utc IS NOT NULL)),
    CHECK(status NOT IN ('completed', 'blocked') OR terminal_at_utc IS NOT NULL)
);

CREATE TABLE positioning_aux_files (
    positioning_aux_file_id TEXT PRIMARY KEY,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    import_session_id TEXT NOT NULL REFERENCES image_import_sessions(import_session_id) ON DELETE RESTRICT,
    positioning_aux_import_item_id TEXT NOT NULL UNIQUE REFERENCES positioning_aux_import_items(positioning_aux_import_item_id) ON DELETE RESTRICT,
    source_preflight_item_id TEXT NOT NULL UNIQUE REFERENCES source_preflight_items(source_preflight_item_id) ON DELETE RESTRICT,
    file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    auxiliary_type TEXT NOT NULL CHECK(auxiliary_type IN ('mrk', 'nav', 'obs', 'rtk')),
    association_policy_version TEXT NOT NULL CHECK(length(association_policy_version) > 0 AND length(association_policy_version) <= 128),
    association_evidence_json TEXT NOT NULL CHECK(
        length(association_evidence_json) > 0 AND length(association_evidence_json) <= 8192 AND
        json_valid(association_evidence_json) AND json_type(association_evidence_json) = 'object'),
    retention_state TEXT NOT NULL CHECK(retention_state IN ('retained')),
    parse_state TEXT NOT NULL CHECK(parse_state IN ('not_attempted', 'unsupported', 'parsed', 'failed')),
    quality_state TEXT NOT NULL CHECK(quality_state IN ('not_checked', 'passed', 'warning', 'failed')),
    parser_schema TEXT NULL CHECK(parser_schema IS NULL OR (length(parser_schema) > 0 AND length(parser_schema) <= 128)),
    parser_profile TEXT NULL CHECK(parser_profile IS NULL OR (length(parser_profile) > 0 AND length(parser_profile) <= 128)),
    parser_name TEXT NULL CHECK(parser_name IS NULL OR (length(parser_name) > 0 AND length(parser_name) <= 128)),
    parser_version TEXT NULL CHECK(parser_version IS NULL OR (length(parser_version) > 0 AND length(parser_version) <= 128)),
    parse_inventory_sha256 TEXT NULL CHECK(parse_inventory_sha256 IS NULL OR (length(parse_inventory_sha256) = 64 AND parse_inventory_sha256 NOT GLOB '*[^0-9a-f]*')),
    parsed_summary_json TEXT NULL CHECK(
        parsed_summary_json IS NULL OR
        (length(parsed_summary_json) > 0 AND length(parsed_summary_json) <= 16384 AND
         json_valid(parsed_summary_json) AND json_type(parsed_summary_json) = 'object')),
    failure_code TEXT NULL CHECK(failure_code IS NULL OR (length(failure_code) > 0 AND length(failure_code) <= 128)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    parsed_at_utc TEXT NULL,
    CHECK(parse_state <> 'not_attempted' OR (
        quality_state = 'not_checked' AND parser_schema IS NULL AND parser_profile IS NULL AND
        parser_name IS NULL AND parser_version IS NULL AND parse_inventory_sha256 IS NULL AND
        parsed_summary_json IS NULL AND failure_code IS NULL AND parsed_at_utc IS NULL)),
    CHECK(parse_state <> 'unsupported' OR (
        quality_state = 'not_checked' AND parser_schema IS NOT NULL AND parser_profile IS NOT NULL AND
        parser_name IS NOT NULL AND parser_version IS NOT NULL AND parse_inventory_sha256 IS NULL AND
        parsed_summary_json IS NULL AND failure_code IS NULL AND parsed_at_utc IS NULL)),
    CHECK(parse_state <> 'failed' OR (
        quality_state = 'failed' AND parser_schema IS NOT NULL AND parser_profile IS NOT NULL AND
        parser_name IS NOT NULL AND parser_version IS NOT NULL AND parse_inventory_sha256 IS NULL AND
        parsed_summary_json IS NULL AND failure_code IS NOT NULL AND parsed_at_utc IS NULL)),
    CHECK(parse_state <> 'parsed' OR (
        quality_state IN ('passed', 'warning', 'failed') AND parser_schema IS NOT NULL AND
        parser_profile IS NOT NULL AND parser_name IS NOT NULL AND parser_version IS NOT NULL AND
        parse_inventory_sha256 IS NOT NULL AND parsed_summary_json IS NOT NULL AND
        failure_code IS NULL AND parsed_at_utc IS NOT NULL)),
    CHECK(auxiliary_type = 'mrk' OR parse_state <> 'parsed')
);

CREATE TABLE positioning_aux_usage (
    positioning_aux_usage_id TEXT PRIMARY KEY,
    positioning_aux_file_id TEXT NOT NULL REFERENCES positioning_aux_files(positioning_aux_file_id) ON DELETE RESTRICT,
    job_execution_id TEXT NOT NULL REFERENCES job_executions(job_execution_id) ON DELETE RESTRICT,
    usage_state TEXT NOT NULL CHECK(usage_state IN ('used', 'rejected')),
    evidence_schema TEXT NOT NULL CHECK(evidence_schema = 'positioning-aux-usage.v1'),
    use_role TEXT NOT NULL CHECK(use_role = 'positioning_aux'),
    content_hash_snapshot TEXT NOT NULL CHECK(length(content_hash_snapshot) = 64 AND content_hash_snapshot NOT GLOB '*[^0-9a-f]*'),
    parse_inventory_sha256_snapshot TEXT NULL CHECK(parse_inventory_sha256_snapshot IS NULL OR (length(parse_inventory_sha256_snapshot) = 64 AND parse_inventory_sha256_snapshot NOT GLOB '*[^0-9a-f]*')),
    evidence_json TEXT NOT NULL CHECK(
        length(evidence_json) > 0 AND length(evidence_json) <= 8192 AND
        json_valid(evidence_json) AND json_type(evidence_json) = 'object'),
    recorded_at_utc TEXT NOT NULL,
    UNIQUE(positioning_aux_file_id, job_execution_id)
);

CREATE INDEX idx_positioning_aux_import_runs_dataset
ON positioning_aux_import_runs(dataset_version_id, status, updated_at_utc, positioning_aux_import_run_id);

CREATE INDEX idx_positioning_aux_import_items_run
ON positioning_aux_import_items(positioning_aux_import_run_id, sort_index, positioning_aux_import_item_id);

CREATE INDEX idx_positioning_aux_files_dataset
ON positioning_aux_files(dataset_version_id, auxiliary_type, retention_state, parse_state, quality_state);

CREATE INDEX idx_positioning_usage_execution
ON positioning_aux_usage(job_execution_id, usage_state);

CREATE TRIGGER validate_positioning_aux_import_run_insert
BEFORE INSERT ON positioning_aux_import_runs
WHEN NOT EXISTS (
    SELECT 1
    FROM image_import_sessions s
    JOIN dataset_versions dv ON dv.dataset_version_id = s.dataset_version_id
    JOIN source_preflight_runs r ON r.source_preflight_run_id = NEW.source_preflight_run_id
    WHERE s.import_session_id = NEW.import_session_id
      AND s.dataset_version_id = NEW.dataset_version_id
      AND dv.lifecycle_state = 'draft'
      AND dv.source_eligibility_state = 'dji_supported'
      AND dv.source_eligibility_run_id = NEW.source_preflight_run_id
      AND r.import_session_id = NEW.import_session_id
      AND r.dataset_version_id = NEW.dataset_version_id
      AND r.status = 'completed'
      AND r.decision = 'dji_supported')
BEGIN
    SELECT RAISE(ABORT, 'positioning aux import requires a completed dji_supported preflight for the same draft dataset');
END;

CREATE TRIGGER validate_positioning_aux_import_run_update
BEFORE UPDATE ON positioning_aux_import_runs
WHEN NEW.positioning_aux_import_run_id IS NOT OLD.positioning_aux_import_run_id
   OR NEW.import_session_id IS NOT OLD.import_session_id
   OR NEW.dataset_version_id IS NOT OLD.dataset_version_id
   OR NEW.source_preflight_run_id IS NOT OLD.source_preflight_run_id
   OR NEW.association_policy_version IS NOT OLD.association_policy_version
   OR NEW.parser_profile IS NOT OLD.parser_profile
   OR NEW.parser_version IS NOT OLD.parser_version
   OR OLD.status IN ('completed', 'blocked', 'cancelled')
   OR NOT ((OLD.status = NEW.status) OR
        (OLD.status = 'pending' AND NEW.status IN ('running', 'blocked', 'interrupted', 'cancelled')) OR
        (OLD.status = 'running' AND NEW.status IN ('completed', 'blocked', 'interrupted', 'cancelled')) OR
        (OLD.status = 'interrupted' AND NEW.status IN ('running', 'blocked', 'cancelled')))
BEGIN
    SELECT RAISE(ABORT, 'positioning aux import run transition or identity is invalid');
END;

CREATE TRIGGER immutable_positioning_aux_import_run_delete
BEFORE DELETE ON positioning_aux_import_runs
WHEN OLD.status IN ('completed', 'blocked', 'cancelled')
BEGIN
    SELECT RAISE(ABORT, 'terminal positioning aux import runs are immutable');
END;

CREATE TRIGGER validate_positioning_aux_import_item_insert
BEFORE INSERT ON positioning_aux_import_items
WHEN NOT EXISTS (
    SELECT 1
    FROM positioning_aux_import_runs r
    JOIN source_preflight_items i ON i.source_preflight_item_id = NEW.source_preflight_item_id
    WHERE r.positioning_aux_import_run_id = NEW.positioning_aux_import_run_id
      AND r.status IN ('pending', 'running')
      AND i.source_preflight_run_id = r.source_preflight_run_id
      AND i.import_session_id = r.import_session_id
      AND i.dataset_version_id = r.dataset_version_id
      AND i.candidate_kind = 'positioning_aux_candidate'
      AND i.status = 'completed'
      AND i.source_entry_key = NEW.source_entry_key
      AND i.display_name = NEW.display_name
      AND i.sort_index = NEW.sort_index
      AND i.format_hint = NEW.auxiliary_type
      AND i.byte_length_snapshot = NEW.byte_length_snapshot
      AND i.source_last_write_time_utc IS NEW.source_last_write_time_utc
      AND i.source_identity_key = NEW.source_identity_key)
BEGIN
    SELECT RAISE(ABORT, 'positioning aux import item must match one completed sidecar preflight item');
END;

CREATE TRIGGER validate_positioning_aux_import_item_update
BEFORE UPDATE ON positioning_aux_import_items
WHEN NEW.positioning_aux_import_item_id IS NOT OLD.positioning_aux_import_item_id
   OR NEW.positioning_aux_import_run_id IS NOT OLD.positioning_aux_import_run_id
   OR NEW.source_preflight_item_id IS NOT OLD.source_preflight_item_id
   OR NEW.source_entry_key IS NOT OLD.source_entry_key
   OR NEW.display_name IS NOT OLD.display_name
   OR NEW.sort_index IS NOT OLD.sort_index
   OR NEW.auxiliary_type IS NOT OLD.auxiliary_type
   OR NEW.byte_length_snapshot IS NOT OLD.byte_length_snapshot
   OR NEW.source_last_write_time_utc IS NOT OLD.source_last_write_time_utc
   OR NEW.source_identity_key IS NOT OLD.source_identity_key
   OR NEW.association_item_count IS NOT OLD.association_item_count
   OR OLD.status IN ('completed', 'blocked')
   OR NOT ((OLD.status = NEW.status) OR
        (OLD.status = 'pending' AND NEW.status IN ('staging', 'blocked', 'interrupted')) OR
        (OLD.status = 'staging' AND NEW.status IN ('staged', 'blocked', 'interrupted')) OR
        (OLD.status = 'staged' AND NEW.status IN ('publishing', 'blocked', 'interrupted')) OR
        (OLD.status = 'publishing' AND NEW.status IN ('retained', 'blocked', 'interrupted')) OR
        (OLD.status = 'retained' AND NEW.status IN ('parsing', 'completed', 'blocked', 'interrupted')) OR
        (OLD.status = 'parsing' AND NEW.status IN ('completed', 'blocked', 'interrupted')) OR
        (OLD.status = 'interrupted' AND NEW.status IN ('staging', 'publishing', 'parsing', 'blocked')))
BEGIN
    SELECT RAISE(ABORT, 'positioning aux import item transition or identity is invalid');
END;

CREATE TRIGGER immutable_positioning_aux_import_item_delete
BEFORE DELETE ON positioning_aux_import_items
WHEN OLD.status IN ('completed', 'blocked')
BEGIN
    SELECT RAISE(ABORT, 'terminal positioning aux import items are immutable');
END;

CREATE TRIGGER validate_positioning_aux_file_insert
BEFORE INSERT ON positioning_aux_files
WHEN NOT EXISTS (
    SELECT 1
    FROM positioning_aux_import_items item
    JOIN positioning_aux_import_runs run ON run.positioning_aux_import_run_id = item.positioning_aux_import_run_id
    JOIN source_preflight_items preflight ON preflight.source_preflight_item_id = item.source_preflight_item_id
    JOIN file_objects object ON object.file_object_id = NEW.file_object_id
    JOIN file_object_roles role ON role.file_object_id = object.file_object_id AND role.object_role = 'positioning_aux'
    WHERE item.positioning_aux_import_item_id = NEW.positioning_aux_import_item_id
      AND item.source_preflight_item_id = NEW.source_preflight_item_id
      AND item.auxiliary_type = NEW.auxiliary_type
      AND item.file_object_id = NEW.file_object_id
      AND item.status IN ('publishing', 'retained', 'parsing', 'completed')
      AND run.import_session_id = NEW.import_session_id
      AND run.dataset_version_id = NEW.dataset_version_id
      AND run.association_policy_version = NEW.association_policy_version
      AND preflight.status = 'completed'
      AND preflight.candidate_kind = 'positioning_aux_candidate'
      AND preflight.format_hint = NEW.auxiliary_type
      AND object.storage_state = 'available'
      AND object.content_hash = item.expected_content_hash
      AND object.byte_length = item.expected_byte_length
      AND object.object_key = item.expected_object_key)
BEGIN
    SELECT RAISE(ABORT, 'positioning aux file requires matching preflight, import item, available object, and positioning role');
END;

CREATE TRIGGER validate_positioning_aux_file_update
BEFORE UPDATE ON positioning_aux_files
WHEN NEW.positioning_aux_file_id IS NOT OLD.positioning_aux_file_id
   OR NEW.dataset_version_id IS NOT OLD.dataset_version_id
   OR NEW.import_session_id IS NOT OLD.import_session_id
   OR NEW.positioning_aux_import_item_id IS NOT OLD.positioning_aux_import_item_id
   OR NEW.source_preflight_item_id IS NOT OLD.source_preflight_item_id
   OR NEW.file_object_id IS NOT OLD.file_object_id
   OR NEW.auxiliary_type IS NOT OLD.auxiliary_type
   OR NEW.association_policy_version IS NOT OLD.association_policy_version
   OR NEW.association_evidence_json IS NOT OLD.association_evidence_json
   OR NEW.retention_state IS NOT OLD.retention_state
   OR OLD.parse_state IN ('parsed', 'failed', 'unsupported')
BEGIN
    SELECT RAISE(ABORT, 'positioning aux file identity or terminal parse state is immutable');
END;

CREATE TRIGGER immutable_positioning_aux_file_delete
BEFORE DELETE ON positioning_aux_files
BEGIN
    SELECT RAISE(ABORT, 'positioning aux files are immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_import_run_insert
BEFORE INSERT ON positioning_aux_import_runs
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id = NEW.dataset_version_id AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_import_run_update
BEFORE UPDATE ON positioning_aux_import_runs
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id IN (OLD.dataset_version_id, NEW.dataset_version_id) AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_import_run_delete
BEFORE DELETE ON positioning_aux_import_runs
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id = OLD.dataset_version_id AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_import_item_insert
BEFORE INSERT ON positioning_aux_import_items
WHEN EXISTS (
    SELECT 1
    FROM positioning_aux_import_runs r
    JOIN dataset_versions dv ON dv.dataset_version_id = r.dataset_version_id
    WHERE r.positioning_aux_import_run_id = NEW.positioning_aux_import_run_id
      AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_import_item_update
BEFORE UPDATE ON positioning_aux_import_items
WHEN EXISTS (
    SELECT 1
    FROM positioning_aux_import_runs r
    JOIN dataset_versions dv ON dv.dataset_version_id = r.dataset_version_id
    WHERE r.positioning_aux_import_run_id IN (OLD.positioning_aux_import_run_id, NEW.positioning_aux_import_run_id)
      AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_import_item_delete
BEFORE DELETE ON positioning_aux_import_items
WHEN EXISTS (
    SELECT 1
    FROM positioning_aux_import_runs r
    JOIN dataset_versions dv ON dv.dataset_version_id = r.dataset_version_id
    WHERE r.positioning_aux_import_run_id = OLD.positioning_aux_import_run_id
      AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_insert
BEFORE INSERT ON positioning_aux_files
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id = NEW.dataset_version_id AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_positioning_update
BEFORE UPDATE ON positioning_aux_files
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id IN (OLD.dataset_version_id, NEW.dataset_version_id) AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER validate_positioning_usage_execution
BEFORE INSERT ON positioning_aux_usage
WHEN NOT EXISTS (
    SELECT 1
    FROM positioning_aux_files p
    JOIN file_objects f ON f.file_object_id = p.file_object_id
    JOIN job_executions e ON e.job_execution_id = NEW.job_execution_id
    JOIN processing_jobs j ON j.processing_job_id = e.processing_job_id
    WHERE p.positioning_aux_file_id = NEW.positioning_aux_file_id
      AND p.dataset_version_id = j.dataset_version_id
      AND f.storage_state = 'available'
      AND f.content_hash = NEW.content_hash_snapshot
      AND (NEW.usage_state <> 'used' OR (
          p.retention_state = 'retained'
          AND p.parse_state = 'parsed'
          AND p.quality_state IN ('passed', 'warning')
          AND p.parse_inventory_sha256 = NEW.parse_inventory_sha256_snapshot)))
BEGIN
    SELECT RAISE(ABORT, 'positioning usage must match execution dataset and retained parsed aux evidence');
END;

CREATE TRIGGER immutable_positioning_usage_update
BEFORE UPDATE ON positioning_aux_usage
BEGIN
    SELECT RAISE(ABORT, 'positioning usage evidence is immutable');
END;

CREATE TRIGGER immutable_positioning_usage_delete
BEFORE DELETE ON positioning_aux_usage
BEGIN
    SELECT RAISE(ABORT, 'positioning usage evidence is immutable');
END;
