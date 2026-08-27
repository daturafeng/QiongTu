CREATE TABLE image_import_sessions (
    import_session_id TEXT PRIMARY KEY,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    source_root_key TEXT NOT NULL CHECK(length(source_root_key) = 64 AND source_root_key NOT GLOB '*[^0-9a-f]*'),
    source_locator_manifest_id TEXT NOT NULL CHECK(
        length(source_locator_manifest_id) > 0 AND length(source_locator_manifest_id) <= 128 AND
        instr(source_locator_manifest_id, '/') = 0 AND instr(source_locator_manifest_id, '\') = 0 AND
        instr(source_locator_manifest_id, ':') = 0 AND instr(source_locator_manifest_id, '..') = 0 AND
        source_locator_manifest_id <> '.'),
    status TEXT NOT NULL CHECK(status IN (
        'awaiting_source_preflight', 'awaiting_source', 'ready',
        'staging', 'publishing', 'completed', 'cancelling', 'cancelled', 'failed')),
    last_error_code TEXT NULL CHECK(last_error_code IS NULL OR (length(last_error_code) > 0 AND length(last_error_code) <= 128)),
    total_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(total_entry_count >= 0),
    available_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(available_entry_count >= 0),
    duplicate_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(duplicate_entry_count >= 0),
    failed_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(failed_entry_count >= 0),
    cancelled_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(cancelled_entry_count >= 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    cancelled_at_utc TEXT NULL,
    UNIQUE(dataset_version_id, source_root_key, source_locator_manifest_id),
    CHECK(status <> 'completed' OR completed_at_utc IS NOT NULL),
    CHECK(status <> 'cancelled' OR cancelled_at_utc IS NOT NULL)
);

CREATE TABLE image_import_entries (
    import_entry_id TEXT PRIMARY KEY,
    import_session_id TEXT NOT NULL REFERENCES image_import_sessions(import_session_id) ON DELETE RESTRICT,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    source_entry_key TEXT NOT NULL CHECK(length(source_entry_key) = 64 AND source_entry_key NOT GLOB '*[^0-9a-f]*'),
    display_name TEXT NOT NULL CHECK(
        length(display_name) > 0 AND length(display_name) <= 255 AND
        instr(display_name, '/') = 0 AND instr(display_name, '\') = 0 AND instr(display_name, ':') = 0),
    sort_index INTEGER NOT NULL CHECK(sort_index >= 0),
    byte_length_snapshot INTEGER NULL CHECK(byte_length_snapshot IS NULL OR byte_length_snapshot >= 0),
    source_last_write_time_utc TEXT NULL,
    source_identity_key TEXT NULL CHECK(source_identity_key IS NULL OR (length(source_identity_key) = 64 AND source_identity_key NOT GLOB '*[^0-9a-f]*')),
    status TEXT NOT NULL CHECK(status IN (
        'discovered', 'awaiting_source_preflight', 'staging', 'staged', 'publishing',
        'available', 'duplicate', 'source_locked', 'source_missing', 'source_unavailable',
        'source_changed', 'integrity_failed', 'storage_full', 'cancelled')),
    failure_code TEXT NULL CHECK(failure_code IS NULL OR (length(failure_code) > 0 AND length(failure_code) <= 128)),
    stage_receipt_id TEXT NULL CHECK(stage_receipt_id IS NULL OR (length(stage_receipt_id) > 0 AND length(stage_receipt_id) <= 128)),
    stage_receipt_sha256 TEXT NULL CHECK(stage_receipt_sha256 IS NULL OR (length(stage_receipt_sha256) = 64 AND stage_receipt_sha256 NOT GLOB '*[^0-9a-f]*')),
    stage_receipt_byte_length INTEGER NULL CHECK(stage_receipt_byte_length IS NULL OR stage_receipt_byte_length >= 0),
    stage_receipt_created_at_utc TEXT NULL,
    expected_content_hash TEXT NULL CHECK(expected_content_hash IS NULL OR (length(expected_content_hash) = 64 AND expected_content_hash NOT GLOB '*[^0-9a-f]*')),
    expected_byte_length INTEGER NULL CHECK(expected_byte_length IS NULL OR expected_byte_length >= 0),
    expected_object_key TEXT NULL CHECK(expected_object_key IS NULL OR expected_object_key GLOB 'sha256/[0-9a-f][0-9a-f]/*'),
    file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    canonical_entry_id TEXT NULL REFERENCES image_import_entries(import_entry_id) ON DELETE RESTRICT,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    terminal_at_utc TEXT NULL,
    UNIQUE(import_session_id, source_entry_key),
    UNIQUE(import_session_id, sort_index),
    CHECK(canonical_entry_id IS NULL OR canonical_entry_id <> import_entry_id),
    CHECK((stage_receipt_id IS NULL AND stage_receipt_sha256 IS NULL AND stage_receipt_byte_length IS NULL AND stage_receipt_created_at_utc IS NULL) OR
          (stage_receipt_id IS NOT NULL AND stage_receipt_sha256 IS NOT NULL AND stage_receipt_byte_length IS NOT NULL AND stage_receipt_created_at_utc IS NOT NULL)),
    CHECK((expected_content_hash IS NULL AND expected_byte_length IS NULL AND expected_object_key IS NULL) OR
          (expected_content_hash IS NOT NULL AND expected_byte_length IS NOT NULL AND expected_object_key = 'sha256/' || substr(expected_content_hash, 1, 2) || '/' || expected_content_hash)),
    CHECK(status NOT IN ('staged', 'publishing', 'available', 'duplicate') OR stage_receipt_id IS NOT NULL),
    CHECK(status NOT IN ('publishing', 'available', 'duplicate') OR expected_content_hash IS NOT NULL),
    CHECK(status NOT IN ('publishing', 'available', 'duplicate') OR
          (stage_receipt_sha256 = expected_content_hash AND stage_receipt_byte_length = expected_byte_length)),
    CHECK(status NOT IN ('available', 'duplicate', 'cancelled', 'integrity_failed', 'storage_full') OR terminal_at_utc IS NOT NULL),
    CHECK(status <> 'available' OR (file_object_id IS NOT NULL AND canonical_entry_id IS NULL)),
    CHECK(status <> 'duplicate' OR (file_object_id IS NOT NULL AND canonical_entry_id IS NOT NULL)),
    CHECK(status NOT IN ('source_locked', 'source_missing', 'source_unavailable', 'source_changed', 'integrity_failed', 'storage_full') OR failure_code IS NOT NULL)
);

CREATE INDEX idx_image_import_sessions_dataset ON image_import_sessions(dataset_version_id, created_at_utc DESC, import_session_id DESC);
CREATE INDEX idx_image_import_entries_session ON image_import_entries(import_session_id, sort_index, import_entry_id);
CREATE INDEX idx_image_import_entries_dataset_status ON image_import_entries(dataset_version_id, status);

CREATE TRIGGER validate_image_import_session_dataset_insert
BEFORE INSERT ON image_import_sessions
WHEN NOT EXISTS (
    SELECT 1 FROM dataset_versions
    WHERE dataset_version_id = NEW.dataset_version_id
      AND lifecycle_state = 'draft')
BEGIN
    SELECT RAISE(ABORT, 'image import session requires a draft dataset version');
END;

CREATE TRIGGER validate_image_import_session_dataset_update
BEFORE UPDATE ON image_import_sessions
WHEN NEW.dataset_version_id IS NOT OLD.dataset_version_id OR NOT EXISTS (
    SELECT 1 FROM dataset_versions
    WHERE dataset_version_id = NEW.dataset_version_id
      AND lifecycle_state = 'draft')
BEGIN
    SELECT RAISE(ABORT, 'image import session requires a stable draft dataset version');
END;

CREATE TRIGGER immutable_terminal_image_import_session
BEFORE UPDATE ON image_import_sessions
WHEN OLD.status IN ('completed', 'cancelled', 'failed') AND NEW.status IS NOT OLD.status
BEGIN
    SELECT RAISE(ABORT, 'terminal image import session cannot transition');
END;

CREATE TRIGGER validate_image_import_entry_session_insert
BEFORE INSERT ON image_import_entries
WHEN NOT EXISTS (
    SELECT 1 FROM image_import_sessions s
    JOIN dataset_versions dv ON dv.dataset_version_id = s.dataset_version_id
    WHERE s.import_session_id = NEW.import_session_id
      AND s.dataset_version_id = NEW.dataset_version_id
      AND dv.lifecycle_state = 'draft')
BEGIN
    SELECT RAISE(ABORT, 'image import entry must belong to the same draft dataset version as its session');
END;

CREATE TRIGGER validate_image_import_entry_session_update
BEFORE UPDATE ON image_import_entries
WHEN NEW.import_session_id IS NOT OLD.import_session_id
   OR NEW.dataset_version_id IS NOT OLD.dataset_version_id
   OR NOT EXISTS (
        SELECT 1 FROM image_import_sessions s
        JOIN dataset_versions dv ON dv.dataset_version_id = s.dataset_version_id
        WHERE s.import_session_id = NEW.import_session_id
          AND s.dataset_version_id = NEW.dataset_version_id
          AND dv.lifecycle_state = 'draft')
BEGIN
    SELECT RAISE(ABORT, 'image import entry must keep the same draft dataset version as its session');
END;

CREATE TRIGGER validate_image_import_available_file_insert
BEFORE INSERT ON image_import_entries
WHEN NEW.file_object_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM file_objects f
    WHERE f.file_object_id = NEW.file_object_id
      AND f.object_kind = 'source_image'
      AND f.storage_state = 'available'
      AND f.content_hash = COALESCE(NEW.expected_content_hash, f.content_hash)
      AND f.byte_length = COALESCE(NEW.expected_byte_length, f.byte_length))
BEGIN
    SELECT RAISE(ABORT, 'image import entry file reference must be an available source image object');
END;

CREATE TRIGGER validate_image_import_available_file_update
BEFORE UPDATE ON image_import_entries
WHEN NEW.file_object_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM file_objects f
    WHERE f.file_object_id = NEW.file_object_id
      AND f.object_kind = 'source_image'
      AND f.storage_state = 'available'
      AND f.content_hash = COALESCE(NEW.expected_content_hash, f.content_hash)
      AND f.byte_length = COALESCE(NEW.expected_byte_length, f.byte_length))
BEGIN
    SELECT RAISE(ABORT, 'image import entry file reference must be an available source image object');
END;

CREATE TRIGGER validate_image_import_canonical_insert
BEFORE INSERT ON image_import_entries
WHEN NEW.canonical_entry_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM image_import_entries c
    WHERE c.import_entry_id = NEW.canonical_entry_id
      AND c.import_session_id = NEW.import_session_id
      AND c.dataset_version_id = NEW.dataset_version_id
      AND c.status = 'available'
      AND c.file_object_id = NEW.file_object_id)
BEGIN
    SELECT RAISE(ABORT, 'duplicate image import entry must reference an available canonical entry in the same session and dataset version');
END;

CREATE TRIGGER validate_image_import_canonical_update
BEFORE UPDATE ON image_import_entries
WHEN NEW.canonical_entry_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM image_import_entries c
    WHERE c.import_entry_id = NEW.canonical_entry_id
      AND c.import_session_id = NEW.import_session_id
      AND c.dataset_version_id = NEW.dataset_version_id
      AND c.status = 'available'
      AND c.file_object_id = NEW.file_object_id)
BEGIN
    SELECT RAISE(ABORT, 'duplicate image import entry must reference an available canonical entry in the same session and dataset version');
END;

CREATE TRIGGER immutable_terminal_image_import_entry
BEFORE UPDATE ON image_import_entries
WHEN OLD.status IN ('available', 'duplicate', 'cancelled', 'integrity_failed', 'storage_full')
BEGIN
    SELECT RAISE(ABORT, 'terminal image import entry is immutable');
END;
