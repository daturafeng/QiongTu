CREATE TABLE image_manifest_upgrade_guard (
    existing_image_count INTEGER NOT NULL CHECK(existing_image_count = 0)
);

INSERT INTO image_manifest_upgrade_guard(existing_image_count)
SELECT count(*) FROM images;

DROP TABLE image_manifest_upgrade_guard;

CREATE TABLE file_object_roles (
    file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    object_role TEXT NOT NULL CHECK(object_role IN (
        'source_image', 'normalized_image_frame', 'positioning_aux', 'input_manifest',
        'checkpoint', 'engine_intermediate', 'formal_output', 'browse_derivative',
        'export_package', 'quality_report', 'log')),
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY(file_object_id, object_role)
);

INSERT INTO file_object_roles(file_object_id, object_role, created_at_utc)
SELECT file_object_id, object_kind, created_at_utc FROM file_objects;

CREATE INDEX idx_file_object_roles_role ON file_object_roles(object_role, file_object_id);

DROP TRIGGER validate_image_import_available_file_insert;
DROP TRIGGER validate_image_import_available_file_update;

CREATE TRIGGER validate_image_import_available_file_insert
BEFORE INSERT ON image_import_entries
WHEN NEW.file_object_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM file_objects f
    JOIN file_object_roles r ON r.file_object_id = f.file_object_id
    WHERE f.file_object_id = NEW.file_object_id AND r.object_role = 'source_image'
      AND f.storage_state = 'available'
      AND f.content_hash = COALESCE(NEW.expected_content_hash, f.content_hash)
      AND f.byte_length = COALESCE(NEW.expected_byte_length, f.byte_length))
BEGIN
    SELECT RAISE(ABORT, 'image import entry file reference must have an available source image role');
END;

CREATE TRIGGER validate_image_import_available_file_update
BEFORE UPDATE ON image_import_entries
WHEN NEW.file_object_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM file_objects f
    JOIN file_object_roles r ON r.file_object_id = f.file_object_id
    WHERE f.file_object_id = NEW.file_object_id AND r.object_role = 'source_image'
      AND f.storage_state = 'available'
      AND f.content_hash = COALESCE(NEW.expected_content_hash, f.content_hash)
      AND f.byte_length = COALESCE(NEW.expected_byte_length, f.byte_length))
BEGIN
    SELECT RAISE(ABORT, 'image import entry file reference must have an available source image role');
END;

CREATE TRIGGER immutable_file_object_role_update
BEFORE UPDATE ON file_object_roles
BEGIN
    SELECT RAISE(ABORT, 'file object roles are immutable');
END;

CREATE TRIGGER immutable_file_object_role_delete
BEFORE DELETE ON file_object_roles
BEGIN
    SELECT RAISE(ABORT, 'file object roles are immutable');
END;

ALTER TABLE images ADD COLUMN import_entry_id TEXT NULL REFERENCES image_import_entries(import_entry_id) ON DELETE RESTRICT;
ALTER TABLE images ADD COLUMN inspection_run_id TEXT NULL;
ALTER TABLE images ADD COLUMN parser_schema TEXT NULL CHECK(parser_schema IS NULL OR (length(parser_schema) > 0 AND length(parser_schema) <= 128));
ALTER TABLE images ADD COLUMN parser_profile TEXT NULL CHECK(parser_profile IS NULL OR (length(parser_profile) > 0 AND length(parser_profile) <= 128));
ALTER TABLE images ADD COLUMN product_parser TEXT NULL CHECK(product_parser IS NULL OR (length(product_parser) > 0 AND length(product_parser) <= 128));
ALTER TABLE images ADD COLUMN product_parser_version TEXT NULL CHECK(product_parser_version IS NULL OR (length(product_parser_version) > 0 AND length(product_parser_version) <= 128));
ALTER TABLE images ADD COLUMN native_decoder TEXT NULL CHECK(native_decoder IS NULL OR (length(native_decoder) > 0 AND length(native_decoder) <= 128));
ALTER TABLE images ADD COLUMN native_decoder_version TEXT NULL CHECK(native_decoder_version IS NULL OR (length(native_decoder_version) > 0 AND length(native_decoder_version) <= 128));
ALTER TABLE images ADD COLUMN main_frame_policy_version TEXT NULL CHECK(main_frame_policy_version IS NULL OR (length(main_frame_policy_version) > 0 AND length(main_frame_policy_version) <= 128));
ALTER TABLE images ADD COLUMN frame_inventory_sha256 TEXT NULL CHECK(frame_inventory_sha256 IS NULL OR (length(frame_inventory_sha256) = 64 AND frame_inventory_sha256 NOT GLOB '*[^0-9a-f]*'));

ALTER TABLE image_frames ADD COLUMN frame_kind TEXT NULL CHECK(frame_kind IS NULL OR frame_kind IN ('jpeg', 'mp_primary_image', 'mp_auxiliary_image', 'tiff_page'));
ALTER TABLE image_frames ADD COLUMN byte_offset INTEGER NULL CHECK(byte_offset IS NULL OR byte_offset >= 0);
ALTER TABLE image_frames ADD COLUMN byte_length INTEGER NULL CHECK(byte_length IS NULL OR byte_length >= 0);
ALTER TABLE image_frames ADD COLUMN bits_per_channel INTEGER NULL CHECK(bits_per_channel IS NULL OR (bits_per_channel > 0 AND bits_per_channel <= 64));
ALTER TABLE image_frames ADD COLUMN orientation INTEGER NULL CHECK(orientation IS NULL OR (orientation >= 1 AND orientation <= 8));
ALTER TABLE image_frames ADD COLUMN effective_width INTEGER NULL CHECK(effective_width IS NULL OR effective_width > 0);
ALTER TABLE image_frames ADD COLUMN effective_height INTEGER NULL CHECK(effective_height IS NULL OR effective_height > 0);
ALTER TABLE image_frames ADD COLUMN normalization_action TEXT NULL CHECK(normalization_action IS NULL OR normalization_action IN (
    'reuse_source_object', 'reuse_source_tiff_page', 'byte_exact_mpo_extract', 'not_selected'));

CREATE TABLE image_inspection_runs (
    inspection_run_id TEXT PRIMARY KEY,
    import_entry_id TEXT NOT NULL UNIQUE REFERENCES image_import_entries(import_entry_id) ON DELETE RESTRICT,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    source_file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    status TEXT NOT NULL CHECK(status IN (
        'pending', 'probing', 'staged', 'publishing', 'recording', 'completed', 'blocked', 'interrupted')),
    parser_schema TEXT NOT NULL CHECK(length(parser_schema) > 0 AND length(parser_schema) <= 128),
    parser_profile TEXT NOT NULL CHECK(length(parser_profile) > 0 AND length(parser_profile) <= 128),
    product_parser TEXT NOT NULL CHECK(length(product_parser) > 0 AND length(product_parser) <= 128),
    product_parser_version TEXT NOT NULL CHECK(length(product_parser_version) > 0 AND length(product_parser_version) <= 128),
    native_decoder TEXT NOT NULL CHECK(length(native_decoder) > 0 AND length(native_decoder) <= 128),
    native_decoder_version TEXT NOT NULL CHECK(length(native_decoder_version) > 0 AND length(native_decoder_version) <= 128),
    main_frame_policy_version TEXT NOT NULL CHECK(length(main_frame_policy_version) > 0 AND length(main_frame_policy_version) <= 128),
    content_container TEXT NULL CHECK(content_container IS NULL OR content_container IN ('jpeg', 'mpo', 'tiff')),
    primary_frame_index INTEGER NULL CHECK(primary_frame_index IS NULL OR primary_frame_index >= 0),
    frame_count INTEGER NULL CHECK(frame_count IS NULL OR frame_count > 0),
    frame_inventory_json TEXT NULL CHECK(frame_inventory_json IS NULL OR (length(frame_inventory_json) <= 65536 AND json_valid(frame_inventory_json))),
    frame_inventory_sha256 TEXT NULL CHECK(frame_inventory_sha256 IS NULL OR (length(frame_inventory_sha256) = 64 AND frame_inventory_sha256 NOT GLOB '*[^0-9a-f]*')),
    normalization_action TEXT NULL CHECK(normalization_action IS NULL OR normalization_action IN (
        'reuse_source_object', 'reuse_source_tiff_page', 'byte_exact_mpo_extract')),
    normalized_stage_id TEXT NULL CHECK(normalized_stage_id IS NULL OR (length(normalized_stage_id) > 0 AND length(normalized_stage_id) <= 128)),
    normalized_stage_sha256 TEXT NULL CHECK(normalized_stage_sha256 IS NULL OR (length(normalized_stage_sha256) = 64 AND normalized_stage_sha256 NOT GLOB '*[^0-9a-f]*')),
    normalized_stage_byte_length INTEGER NULL CHECK(normalized_stage_byte_length IS NULL OR normalized_stage_byte_length > 0),
    normalized_stage_created_at_utc TEXT NULL,
    normalized_content_sha256 TEXT NULL CHECK(normalized_content_sha256 IS NULL OR (length(normalized_content_sha256) = 64 AND normalized_content_sha256 NOT GLOB '*[^0-9a-f]*')),
    normalized_content_byte_length INTEGER NULL CHECK(normalized_content_byte_length IS NULL OR normalized_content_byte_length > 0),
    normalized_object_key TEXT NULL CHECK(normalized_object_key IS NULL OR (length(normalized_object_key) > 0 AND substr(normalized_object_key, 1, 1) NOT IN ('/', '\') AND instr(normalized_object_key, ':') = 0)),
    image_id TEXT NULL REFERENCES images(image_id) ON DELETE RESTRICT,
    failure_code TEXT NULL CHECK(failure_code IS NULL OR (length(failure_code) > 0 AND length(failure_code) <= 128)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    CHECK(status NOT IN ('staged', 'publishing') OR (
        normalized_stage_id IS NOT NULL AND normalized_stage_sha256 IS NOT NULL AND
        normalized_stage_byte_length IS NOT NULL AND normalized_stage_created_at_utc IS NOT NULL)),
    CHECK(status NOT IN ('publishing', 'recording', 'completed') OR (
        content_container IS NOT NULL AND primary_frame_index IS NOT NULL AND frame_count IS NOT NULL AND
        frame_inventory_json IS NOT NULL AND frame_inventory_sha256 IS NOT NULL AND normalization_action IS NOT NULL AND
        normalized_content_sha256 IS NOT NULL AND normalized_content_byte_length IS NOT NULL AND normalized_object_key IS NOT NULL)),
    CHECK(status <> 'completed' OR (image_id IS NOT NULL AND failure_code IS NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status <> 'blocked' OR (image_id IS NULL AND failure_code IS NOT NULL AND completed_at_utc IS NOT NULL)),
    CHECK(status IN ('completed', 'blocked') OR completed_at_utc IS NULL)
);

CREATE TABLE image_frame_lineage (
    image_frame_lineage_id TEXT PRIMARY KEY,
    image_frame_id TEXT NOT NULL UNIQUE REFERENCES image_frames(image_frame_id) ON DELETE RESTRICT,
    source_file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    normalized_file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    source_frame_index INTEGER NOT NULL CHECK(source_frame_index >= 0),
    normalization_action TEXT NOT NULL CHECK(normalization_action IN (
        'reuse_source_object', 'reuse_source_tiff_page', 'byte_exact_mpo_extract', 'not_selected')),
    parser_schema TEXT NOT NULL CHECK(length(parser_schema) > 0 AND length(parser_schema) <= 128),
    parser_profile TEXT NOT NULL CHECK(length(parser_profile) > 0 AND length(parser_profile) <= 128),
    product_parser TEXT NOT NULL CHECK(length(product_parser) > 0 AND length(product_parser) <= 128),
    product_parser_version TEXT NOT NULL CHECK(length(product_parser_version) > 0 AND length(product_parser_version) <= 128),
    native_decoder TEXT NOT NULL CHECK(length(native_decoder) > 0 AND length(native_decoder) <= 128),
    native_decoder_version TEXT NOT NULL CHECK(length(native_decoder_version) > 0 AND length(native_decoder_version) <= 128),
    main_frame_policy_version TEXT NOT NULL CHECK(length(main_frame_policy_version) > 0 AND length(main_frame_policy_version) <= 128),
    byte_offset INTEGER NOT NULL CHECK(byte_offset >= 0),
    byte_length INTEGER NOT NULL CHECK(byte_length >= 0),
    source_content_hash_snapshot TEXT NOT NULL CHECK(length(source_content_hash_snapshot) = 64 AND source_content_hash_snapshot NOT GLOB '*[^0-9a-f]*'),
    source_byte_length_snapshot INTEGER NOT NULL CHECK(source_byte_length_snapshot >= 0),
    normalized_content_hash_snapshot TEXT NULL CHECK(normalized_content_hash_snapshot IS NULL OR (length(normalized_content_hash_snapshot) = 64 AND normalized_content_hash_snapshot NOT GLOB '*[^0-9a-f]*')),
    normalized_byte_length_snapshot INTEGER NULL CHECK(normalized_byte_length_snapshot IS NULL OR normalized_byte_length_snapshot >= 0),
    lineage_sha256 TEXT NOT NULL CHECK(length(lineage_sha256) = 64 AND lineage_sha256 NOT GLOB '*[^0-9a-f]*'),
    created_at_utc TEXT NOT NULL,
    CHECK((normalized_file_object_id IS NULL AND normalized_content_hash_snapshot IS NULL AND normalized_byte_length_snapshot IS NULL) OR
          (normalized_file_object_id IS NOT NULL AND normalized_content_hash_snapshot IS NOT NULL AND normalized_byte_length_snapshot IS NOT NULL)),
    CHECK((normalization_action IN ('reuse_source_object', 'reuse_source_tiff_page') AND normalized_file_object_id = source_file_object_id) OR
          (normalization_action = 'byte_exact_mpo_extract' AND normalized_file_object_id IS NOT NULL AND byte_length > 0) OR
          (normalization_action = 'not_selected' AND normalized_file_object_id IS NULL))
);

CREATE UNIQUE INDEX ux_images_import_entry ON images(import_entry_id);
CREATE UNIQUE INDEX ux_images_inspection_run ON images(inspection_run_id);
CREATE INDEX idx_image_inspection_runs_status ON image_inspection_runs(status, updated_at_utc, inspection_run_id);
CREATE INDEX idx_image_frame_lineage_source ON image_frame_lineage(source_file_object_id, source_frame_index);
CREATE INDEX idx_image_frame_lineage_identity ON image_frame_lineage(lineage_sha256);

CREATE TRIGGER validate_image_inspection_source_insert
BEFORE INSERT ON image_inspection_runs
WHEN NOT EXISTS (
    SELECT 1 FROM image_import_entries e
    JOIN file_objects f ON f.file_object_id = e.file_object_id
    JOIN file_object_roles r ON r.file_object_id = f.file_object_id AND r.object_role = 'source_image'
    WHERE e.import_entry_id = NEW.import_entry_id AND e.dataset_version_id = NEW.dataset_version_id
      AND e.status = 'available' AND e.canonical_entry_id IS NULL
      AND e.file_object_id = NEW.source_file_object_id AND f.storage_state = 'available')
BEGIN
    SELECT RAISE(ABORT, 'image inspection requires an available canonical source image role');
END;

CREATE TRIGGER validate_image_inspection_transition
BEFORE UPDATE ON image_inspection_runs
WHEN NEW.inspection_run_id IS NOT OLD.inspection_run_id
   OR NEW.import_entry_id IS NOT OLD.import_entry_id
   OR NEW.dataset_version_id IS NOT OLD.dataset_version_id
   OR NEW.source_file_object_id IS NOT OLD.source_file_object_id
   OR NEW.parser_schema IS NOT OLD.parser_schema OR NEW.parser_profile IS NOT OLD.parser_profile
   OR NEW.product_parser IS NOT OLD.product_parser OR NEW.product_parser_version IS NOT OLD.product_parser_version
   OR NEW.native_decoder IS NOT OLD.native_decoder OR NEW.native_decoder_version IS NOT OLD.native_decoder_version
   OR NEW.main_frame_policy_version IS NOT OLD.main_frame_policy_version
   OR OLD.status IN ('completed', 'blocked')
   OR NOT ((OLD.status = NEW.status) OR
        (OLD.status = 'pending' AND NEW.status IN ('probing', 'blocked', 'interrupted')) OR
        (OLD.status = 'probing' AND NEW.status IN ('staged', 'recording', 'blocked', 'interrupted')) OR
        (OLD.status = 'staged' AND NEW.status IN ('publishing', 'blocked', 'interrupted')) OR
        (OLD.status = 'publishing' AND NEW.status IN ('recording', 'blocked', 'interrupted')) OR
        (OLD.status = 'recording' AND NEW.status IN ('completed', 'blocked', 'interrupted')) OR
        (OLD.status = 'interrupted' AND NEW.status IN ('probing', 'staged', 'publishing', 'recording', 'blocked')))
BEGIN
    SELECT RAISE(ABORT, 'image inspection transition or identity is invalid');
END;

CREATE TRIGGER validate_completed_image_inspection
BEFORE UPDATE ON image_inspection_runs
WHEN NEW.status = 'completed' AND OLD.status <> 'completed' AND NOT EXISTS (
    SELECT 1 FROM images i
    JOIN file_object_roles nr ON nr.file_object_id = i.normalized_file_object_id AND nr.object_role = 'normalized_image_frame'
    WHERE i.image_id = NEW.image_id AND i.dataset_version_id = NEW.dataset_version_id
      AND i.source_file_object_id = NEW.source_file_object_id
      AND i.content_container = NEW.content_container AND i.primary_frame_index = NEW.primary_frame_index
      AND i.frame_inventory_sha256 = NEW.frame_inventory_sha256
      AND (SELECT count(*) FROM image_frames f WHERE f.image_id = i.image_id) = NEW.frame_count
      AND (SELECT count(*) FROM image_frames f WHERE f.image_id = i.image_id AND f.frame_role = 'primary_photogrammetry') = 1
      AND EXISTS (SELECT 1 FROM image_frames f WHERE f.image_id = i.image_id AND f.frame_role = 'primary_photogrammetry' AND f.frame_index = NEW.primary_frame_index)
      AND (SELECT count(*) FROM image_frames f JOIN image_frame_lineage l ON l.image_frame_id = f.image_frame_id WHERE f.image_id = i.image_id) = NEW.frame_count
      AND (SELECT min(frame_index) FROM image_frames f WHERE f.image_id = i.image_id) = 0
      AND (SELECT max(frame_index) FROM image_frames f WHERE f.image_id = i.image_id) = NEW.frame_count - 1)
BEGIN
    SELECT RAISE(ABORT, 'completed image inspection requires one complete authoritative manifest');
END;

CREATE TRIGGER immutable_terminal_image_inspection_delete
BEFORE DELETE ON image_inspection_runs
WHEN OLD.status IN ('completed', 'blocked')
BEGIN
    SELECT RAISE(ABORT, 'terminal image inspections are immutable');
END;

CREATE TRIGGER validate_image_manifest_insert
BEFORE INSERT ON images
WHEN NEW.import_entry_id IS NULL OR NEW.inspection_run_id IS NULL OR NEW.parser_schema IS NULL OR NEW.parser_profile IS NULL
   OR NEW.product_parser IS NULL OR NEW.product_parser_version IS NULL OR NEW.native_decoder IS NULL OR NEW.native_decoder_version IS NULL
   OR NEW.main_frame_policy_version IS NULL OR NEW.frame_inventory_sha256 IS NULL OR NEW.primary_frame_index IS NULL
   OR NEW.width IS NULL OR NEW.height IS NULL OR NOT EXISTS (
        SELECT 1 FROM image_inspection_runs r
        JOIN file_object_roles sr ON sr.file_object_id = r.source_file_object_id AND sr.object_role = 'source_image'
        JOIN file_object_roles nr ON nr.file_object_id = NEW.normalized_file_object_id AND nr.object_role = 'normalized_image_frame'
        WHERE r.inspection_run_id = NEW.inspection_run_id AND r.import_entry_id = NEW.import_entry_id
          AND r.dataset_version_id = NEW.dataset_version_id AND r.source_file_object_id = NEW.source_file_object_id
          AND r.status = 'recording' AND r.content_container = NEW.content_container
          AND r.primary_frame_index = NEW.primary_frame_index AND r.frame_inventory_sha256 = NEW.frame_inventory_sha256)
BEGIN
    SELECT RAISE(ABORT, 'image manifest requires a recording canonical inspection and normalized role');
END;

CREATE TRIGGER immutable_image_manifest_identity
BEFORE UPDATE ON images
WHEN OLD.inspection_run_id IS NOT NULL AND (
    NEW.import_entry_id IS NOT OLD.import_entry_id OR NEW.inspection_run_id IS NOT OLD.inspection_run_id OR
    NEW.dataset_version_id IS NOT OLD.dataset_version_id OR NEW.source_file_object_id IS NOT OLD.source_file_object_id OR
    NEW.normalized_file_object_id IS NOT OLD.normalized_file_object_id OR NEW.import_source_key IS NOT OLD.import_source_key OR
    NEW.sort_index IS NOT OLD.sort_index OR NEW.content_container IS NOT OLD.content_container OR
    NEW.primary_frame_index IS NOT OLD.primary_frame_index OR NEW.width IS NOT OLD.width OR NEW.height IS NOT OLD.height OR
    NEW.parser_schema IS NOT OLD.parser_schema OR NEW.parser_profile IS NOT OLD.parser_profile OR
    NEW.product_parser IS NOT OLD.product_parser OR NEW.product_parser_version IS NOT OLD.product_parser_version OR
    NEW.native_decoder IS NOT OLD.native_decoder OR NEW.native_decoder_version IS NOT OLD.native_decoder_version OR
    NEW.main_frame_policy_version IS NOT OLD.main_frame_policy_version OR NEW.frame_inventory_sha256 IS NOT OLD.frame_inventory_sha256)
BEGIN
    SELECT RAISE(ABORT, 'authoritative image manifest identity is immutable');
END;

CREATE TRIGGER immutable_image_manifest_delete
BEFORE DELETE ON images
WHEN OLD.inspection_run_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'authoritative image manifest cannot be deleted');
END;

CREATE TRIGGER validate_image_frame_manifest_insert
BEFORE INSERT ON image_frames
WHEN NEW.frame_kind IS NULL OR NEW.byte_offset IS NULL OR NEW.byte_length IS NULL OR NEW.bits_per_channel IS NULL
   OR NEW.orientation IS NULL OR NEW.effective_width IS NULL OR NEW.effective_height IS NULL OR NEW.normalization_action IS NULL
   OR NOT EXISTS (SELECT 1 FROM images i JOIN image_inspection_runs r ON r.inspection_run_id = i.inspection_run_id
        WHERE i.image_id = NEW.image_id AND r.status = 'recording' AND NEW.frame_index >= 0 AND NEW.frame_index < r.frame_count)
   OR (NEW.frame_role = 'primary_photogrammetry' AND (NEW.normalized_file_object_id IS NULL OR NOT EXISTS (
        SELECT 1 FROM file_object_roles nr WHERE nr.file_object_id = NEW.normalized_file_object_id AND nr.object_role = 'normalized_image_frame')))
   OR (NEW.frame_role <> 'primary_photogrammetry' AND NEW.normalized_file_object_id IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'image frame requires complete bounded manifest fields and normalized role');
END;

CREATE TRIGGER immutable_authoritative_image_frame_update
BEFORE UPDATE ON image_frames
WHEN EXISTS (SELECT 1 FROM images i WHERE i.image_id = OLD.image_id AND i.inspection_run_id IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'authoritative image frames are immutable');
END;

CREATE TRIGGER immutable_authoritative_image_frame_delete
BEFORE DELETE ON image_frames
WHEN EXISTS (SELECT 1 FROM images i WHERE i.image_id = OLD.image_id AND i.inspection_run_id IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'authoritative image frames are immutable');
END;

CREATE TRIGGER validate_image_frame_lineage_insert
BEFORE INSERT ON image_frame_lineage
WHEN NOT EXISTS (
    SELECT 1 FROM image_frames fr
    JOIN images i ON i.image_id = fr.image_id
    JOIN image_inspection_runs r ON r.inspection_run_id = i.inspection_run_id
    JOIN file_objects sf ON sf.file_object_id = NEW.source_file_object_id
    JOIN file_object_roles sr ON sr.file_object_id = sf.file_object_id AND sr.object_role = 'source_image'
    WHERE fr.image_frame_id = NEW.image_frame_id AND r.status = 'recording'
      AND i.source_file_object_id = NEW.source_file_object_id AND fr.frame_index = NEW.source_frame_index
      AND sf.storage_state = 'available' AND sf.content_hash = NEW.source_content_hash_snapshot
      AND sf.byte_length = NEW.source_byte_length_snapshot)
   OR (NEW.normalized_file_object_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM file_objects nf
        JOIN file_object_roles nr ON nr.file_object_id = nf.file_object_id AND nr.object_role = 'normalized_image_frame'
        WHERE nf.file_object_id = NEW.normalized_file_object_id AND nf.storage_state = 'available'
          AND nf.content_hash = NEW.normalized_content_hash_snapshot AND nf.byte_length = NEW.normalized_byte_length_snapshot))
BEGIN
    SELECT RAISE(ABORT, 'image frame lineage snapshots must match available source and normalized roles');
END;

CREATE TRIGGER immutable_image_frame_lineage_update
BEFORE UPDATE ON image_frame_lineage
BEGIN
    SELECT RAISE(ABORT, 'image frame lineage is immutable');
END;

CREATE TRIGGER immutable_image_frame_lineage_delete
BEFORE DELETE ON image_frame_lineage
BEGIN
    SELECT RAISE(ABORT, 'image frame lineage is immutable');
END;
