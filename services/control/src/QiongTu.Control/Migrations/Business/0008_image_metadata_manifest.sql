CREATE TABLE image_metadata_upgrade_guard (
    existing_field_count INTEGER NOT NULL CHECK(existing_field_count = 0)
);

INSERT INTO image_metadata_upgrade_guard(existing_field_count)
SELECT count(*) FROM image_metadata_fields;

DROP TABLE image_metadata_upgrade_guard;

DROP TRIGGER immutable_sealed_image_metadata_insert;
DROP TRIGGER immutable_sealed_image_metadata_update;
DROP TRIGGER immutable_sealed_image_metadata_delete;

DROP TABLE image_metadata_fields;

CREATE TABLE image_metadata_fields (
    image_metadata_field_id TEXT PRIMARY KEY,
    image_id TEXT NOT NULL REFERENCES images(image_id) ON DELETE RESTRICT,
    field_name TEXT NOT NULL,
    field_value_json TEXT NULL,
    source_kind TEXT NOT NULL CHECK(source_kind IN (
        'exif', 'gps_exif', 'dji_xmp', 'sidecar', 'derived', 'user_confirmed')),
    field_state TEXT NOT NULL CHECK(field_state IN (
        'present', 'missing', 'conflict', 'abnormal', 'not_assessable')),
    source_detail TEXT NULL,
    metadata_run_id TEXT NULL REFERENCES image_metadata_runs(metadata_run_id) ON DELETE RESTRICT,
    UNIQUE(image_id, field_name, source_kind)
);

CREATE TABLE image_metadata_runs (
    metadata_run_id TEXT PRIMARY KEY,
    image_id TEXT NOT NULL UNIQUE REFERENCES images(image_id) ON DELETE RESTRICT,
    normalized_file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    status TEXT NOT NULL CHECK(status IN ('pending', 'parsing', 'completed', 'blocked', 'interrupted')),
    parser_schema TEXT NOT NULL CHECK(length(parser_schema) > 0 AND length(parser_schema) <= 128),
    parser_profile TEXT NOT NULL CHECK(length(parser_profile) > 0 AND length(parser_profile) <= 128),
    product_parser TEXT NOT NULL CHECK(length(product_parser) > 0 AND length(product_parser) <= 128),
    product_parser_version TEXT NOT NULL CHECK(length(product_parser_version) > 0 AND length(product_parser_version) <= 128),
    metadata_extractor_version TEXT NOT NULL CHECK(length(metadata_extractor_version) > 0 AND length(metadata_extractor_version) <= 128),
    field_mapping_version TEXT NOT NULL CHECK(length(field_mapping_version) > 0 AND length(field_mapping_version) <= 128),
    conflict_policy_version TEXT NOT NULL CHECK(length(conflict_policy_version) > 0 AND length(conflict_policy_version) <= 128),
    normalized_content_hash_snapshot TEXT NOT NULL CHECK(length(normalized_content_hash_snapshot) = 64 AND normalized_content_hash_snapshot NOT GLOB '*[^0-9a-f]*'),
    normalized_byte_length_snapshot INTEGER NOT NULL CHECK(normalized_byte_length_snapshot > 0),
    field_inventory_sha256 TEXT NULL CHECK(field_inventory_sha256 IS NULL OR (length(field_inventory_sha256) = 64 AND field_inventory_sha256 NOT GLOB '*[^0-9a-f]*')),
    field_count INTEGER NULL CHECK(field_count IS NULL OR field_count > 0),
    failure_code TEXT NULL CHECK(failure_code IS NULL OR (length(failure_code) > 0 AND length(failure_code) <= 128)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    CHECK(status <> 'completed' OR (
        field_inventory_sha256 IS NOT NULL AND field_count IS NOT NULL AND
        failure_code IS NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status <> 'blocked' OR (
        field_inventory_sha256 IS NULL AND field_count IS NULL AND
        failure_code IS NOT NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status IN ('completed', 'blocked') OR completed_at_utc IS NULL)
);

CREATE INDEX idx_image_metadata_runs_status
ON image_metadata_runs(status, updated_at_utc, metadata_run_id);

CREATE INDEX idx_image_metadata_fields_run
ON image_metadata_fields(metadata_run_id, field_name, source_kind);

CREATE TRIGGER validate_image_metadata_run_insert
BEFORE INSERT ON image_metadata_runs
WHEN NOT EXISTS (
    SELECT 1
    FROM images i
    JOIN image_inspection_runs ir ON ir.inspection_run_id = i.inspection_run_id
    JOIN file_objects f ON f.file_object_id = i.normalized_file_object_id
    JOIN file_object_roles r ON r.file_object_id = f.file_object_id AND r.object_role = 'normalized_image_frame'
    WHERE i.image_id = NEW.image_id
      AND i.normalized_file_object_id = NEW.normalized_file_object_id
      AND ir.status = 'completed'
      AND f.storage_state = 'available'
      AND f.content_hash = NEW.normalized_content_hash_snapshot
      AND f.byte_length = NEW.normalized_byte_length_snapshot)
BEGIN
    SELECT RAISE(ABORT, 'image metadata requires a completed image manifest and available normalized object');
END;

CREATE TRIGGER validate_image_metadata_run_transition
BEFORE UPDATE ON image_metadata_runs
WHEN NEW.metadata_run_id IS NOT OLD.metadata_run_id
   OR NEW.image_id IS NOT OLD.image_id
   OR NEW.normalized_file_object_id IS NOT OLD.normalized_file_object_id
   OR NEW.parser_schema IS NOT OLD.parser_schema
   OR NEW.parser_profile IS NOT OLD.parser_profile
   OR NEW.product_parser IS NOT OLD.product_parser
   OR NEW.product_parser_version IS NOT OLD.product_parser_version
   OR NEW.metadata_extractor_version IS NOT OLD.metadata_extractor_version
   OR NEW.field_mapping_version IS NOT OLD.field_mapping_version
   OR NEW.conflict_policy_version IS NOT OLD.conflict_policy_version
   OR NEW.normalized_content_hash_snapshot IS NOT OLD.normalized_content_hash_snapshot
   OR NEW.normalized_byte_length_snapshot IS NOT OLD.normalized_byte_length_snapshot
   OR OLD.status IN ('completed', 'blocked')
   OR NOT ((OLD.status = NEW.status) OR
        (OLD.status = 'pending' AND NEW.status IN ('parsing', 'blocked', 'interrupted')) OR
        (OLD.status = 'parsing' AND NEW.status IN ('completed', 'blocked', 'interrupted')) OR
        (OLD.status = 'interrupted' AND NEW.status IN ('parsing', 'blocked')))
BEGIN
    SELECT RAISE(ABORT, 'image metadata transition or identity is invalid');
END;

CREATE TRIGGER validate_image_metadata_field_insert
BEFORE INSERT ON image_metadata_fields
WHEN (NEW.metadata_run_id IS NULL AND EXISTS (
        SELECT 1 FROM images i WHERE i.image_id = NEW.image_id AND i.inspection_run_id IS NOT NULL))
   OR (NEW.metadata_run_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM image_metadata_runs r
        WHERE r.metadata_run_id = NEW.metadata_run_id AND r.image_id = NEW.image_id AND r.status = 'parsing'))
   OR (NEW.metadata_run_id IS NOT NULL AND NEW.field_name NOT IN (
        'capture.time_local', 'capture.time_utc',
        'camera.manufacturer', 'camera.model', 'camera.lens_model', 'camera.focal_length_mm',
        'position.latitude_deg', 'position.longitude_deg',
        'position.absolute_altitude_m', 'position.relative_altitude_m',
        'pose.gimbal_roll_deg', 'pose.gimbal_pitch_deg', 'pose.gimbal_yaw_deg',
        'pose.flight_roll_deg', 'pose.flight_pitch_deg', 'pose.flight_yaw_deg',
        'position.rtk_flag', 'position.std_lon_m', 'position.std_lat_m', 'position.std_height_m'))
   OR (NEW.metadata_run_id IS NOT NULL AND NEW.source_kind NOT IN ('exif', 'gps_exif', 'dji_xmp', 'derived'))
   OR (NEW.metadata_run_id IS NOT NULL AND (
        NEW.source_detail IS NULL OR length(NEW.source_detail) = 0 OR length(NEW.source_detail) > 128))
   OR (NEW.metadata_run_id IS NOT NULL AND (
        (NEW.field_state IN ('present', 'conflict') AND (
            NEW.field_value_json IS NULL OR length(NEW.field_value_json) > 1024 OR
            NOT json_valid(NEW.field_value_json) OR
            json_type(NEW.field_value_json) NOT IN ('text', 'integer', 'real', 'true', 'false')))
        OR (NEW.field_state IN ('missing', 'abnormal', 'not_assessable') AND NEW.field_value_json IS NOT NULL)))
BEGIN
    SELECT RAISE(ABORT, 'image metadata field is outside the authoritative allowlist');
END;

CREATE TRIGGER validate_completed_image_metadata
BEFORE UPDATE ON image_metadata_runs
WHEN NEW.status = 'completed' AND OLD.status <> 'completed' AND (
    NEW.field_count <> (SELECT count(*) FROM image_metadata_fields f WHERE f.metadata_run_id = NEW.metadata_run_id)
    OR 20 <> (SELECT count(DISTINCT f.field_name) FROM image_metadata_fields f WHERE f.metadata_run_id = NEW.metadata_run_id)
    OR NOT EXISTS (
        SELECT 1 FROM images i
        WHERE i.image_id = NEW.image_id
          AND i.normalized_file_object_id = NEW.normalized_file_object_id
          AND i.metadata_state <> 'not_parsed'
          AND i.raw_metadata_json IS NULL))
BEGIN
    SELECT RAISE(ABORT, 'completed image metadata requires one complete authoritative field inventory');
END;

CREATE TRIGGER validate_blocked_image_metadata
BEFORE UPDATE ON image_metadata_runs
WHEN NEW.status = 'blocked' AND OLD.status <> 'blocked' AND (
    EXISTS (SELECT 1 FROM image_metadata_fields f WHERE f.metadata_run_id = NEW.metadata_run_id)
    OR NOT EXISTS (
        SELECT 1 FROM images i
        WHERE i.image_id = NEW.image_id AND i.metadata_state = 'abnormal' AND i.raw_metadata_json IS NULL))
BEGIN
    SELECT RAISE(ABORT, 'blocked image metadata cannot retain partial fields');
END;

CREATE TRIGGER immutable_terminal_image_metadata_delete
BEFORE DELETE ON image_metadata_runs
WHEN OLD.status IN ('completed', 'blocked')
BEGIN
    SELECT RAISE(ABORT, 'terminal image metadata runs are immutable');
END;

CREATE TRIGGER immutable_authoritative_image_metadata_field_update
BEFORE UPDATE ON image_metadata_fields
WHEN OLD.metadata_run_id IS NOT NULL AND EXISTS (
    SELECT 1 FROM image_metadata_runs r
    WHERE r.metadata_run_id = OLD.metadata_run_id AND r.status IN ('completed', 'blocked'))
BEGIN
    SELECT RAISE(ABORT, 'authoritative image metadata fields are immutable');
END;

CREATE TRIGGER immutable_authoritative_image_metadata_field_delete
BEFORE DELETE ON image_metadata_fields
WHEN OLD.metadata_run_id IS NOT NULL AND EXISTS (
    SELECT 1 FROM image_metadata_runs r
    WHERE r.metadata_run_id = OLD.metadata_run_id AND r.status IN ('completed', 'blocked'))
BEGIN
    SELECT RAISE(ABORT, 'authoritative image metadata fields are immutable');
END;

CREATE TRIGGER immutable_authoritative_image_metadata_summary
BEFORE UPDATE ON images
WHEN EXISTS (
        SELECT 1 FROM image_metadata_runs r
        WHERE r.image_id = OLD.image_id AND r.status IN ('completed', 'blocked'))
   AND (NEW.capture_time_utc IS NOT OLD.capture_time_utc
        OR NEW.manufacturer IS NOT OLD.manufacturer
        OR NEW.camera_model IS NOT OLD.camera_model
        OR NEW.lens_model IS NOT OLD.lens_model
        OR NEW.metadata_state IS NOT OLD.metadata_state
        OR NEW.raw_metadata_json IS NOT OLD.raw_metadata_json)
BEGIN
    SELECT RAISE(ABORT, 'authoritative image metadata summary is immutable');
END;

CREATE TRIGGER immutable_sealed_image_metadata_insert
BEFORE INSERT ON image_metadata_fields
WHEN EXISTS (
    SELECT 1 FROM images i JOIN dataset_versions dv ON dv.dataset_version_id = i.dataset_version_id
    WHERE i.image_id = NEW.image_id AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image metadata is immutable');
END;

CREATE TRIGGER immutable_sealed_image_metadata_update
BEFORE UPDATE ON image_metadata_fields
WHEN EXISTS (
    SELECT 1 FROM images i JOIN dataset_versions dv ON dv.dataset_version_id = i.dataset_version_id
    WHERE i.image_id IN (OLD.image_id, NEW.image_id) AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image metadata is immutable');
END;

CREATE TRIGGER immutable_sealed_image_metadata_delete
BEFORE DELETE ON image_metadata_fields
WHEN EXISTS (
    SELECT 1 FROM images i JOIN dataset_versions dv ON dv.dataset_version_id = i.dataset_version_id
    WHERE i.image_id = OLD.image_id AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image metadata is immutable');
END;
