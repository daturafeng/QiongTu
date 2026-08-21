CREATE TABLE crs_definitions (
    crs_id TEXT PRIMARY KEY,
    authority TEXT NULL,
    code TEXT NULL,
    name TEXT NOT NULL,
    wkt TEXT NULL,
    projjson TEXT NULL,
    horizontal_unit TEXT NOT NULL,
    vertical_reference TEXT NULL,
    axis_order TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    CHECK(authority IS NOT NULL OR wkt IS NOT NULL OR projjson IS NOT NULL),
    UNIQUE(authority, code, vertical_reference)
);

CREATE TABLE projects (
    project_id TEXT PRIMARY KEY,
    name TEXT NOT NULL CHECK(length(trim(name)) > 0),
    description TEXT NULL,
    default_crs_id TEXT NULL REFERENCES crs_definitions(crs_id) ON DELETE RESTRICT,
    suggested_crs_id TEXT NULL REFERENCES crs_definitions(crs_id) ON DELETE RESTRICT,
    spatial_configuration_state TEXT NOT NULL
        CHECK(spatial_configuration_state IN ('pending', 'suggested', 'confirmed', 'insufficient_metadata')),
    lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN ('active', 'archived')),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE datasets (
    dataset_id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    name TEXT NOT NULL CHECK(length(trim(name)) > 0),
    description TEXT NULL,
    lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN ('active', 'archived')),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE(project_id, name)
);

CREATE TABLE file_objects (
    file_object_id TEXT PRIMARY KEY,
    object_kind TEXT NOT NULL CHECK(object_kind IN (
        'source_image', 'normalized_image_frame', 'positioning_aux', 'input_manifest',
        'checkpoint', 'engine_intermediate', 'formal_output', 'browse_derivative',
        'export_package', 'quality_report', 'log')),
    hash_algorithm TEXT NOT NULL CHECK(hash_algorithm IN ('sha256')),
    content_hash TEXT NOT NULL CHECK(length(content_hash) = 64 AND content_hash NOT GLOB '*[^0-9a-f]*'),
    byte_length INTEGER NOT NULL CHECK(byte_length >= 0),
    media_type TEXT NULL,
    object_key TEXT NULL,
    storage_state TEXT NOT NULL CHECK(storage_state IN (
        'registered', 'pending_copy', 'available', 'quarantined', 'missing', 'deleted')),
    original_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL,
    available_at_utc TEXT NULL,
    CHECK(object_key IS NULL OR (
        length(object_key) > 0 AND substr(object_key, 1, 1) NOT IN ('/', '\') AND instr(object_key, ':') = 0)),
    UNIQUE(hash_algorithm, content_hash, byte_length)
);

CREATE TABLE dataset_versions (
    dataset_version_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES datasets(dataset_id) ON DELETE RESTRICT,
    version_number INTEGER NOT NULL CHECK(version_number > 0),
    parent_version_id TEXT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN ('draft', 'sealed', 'retired')),
    source_eligibility_state TEXT NOT NULL CHECK(source_eligibility_state IN (
        'pending', 'dji_supported', 'out_of_scope', 'unconfirmed')),
    quality_gate_state TEXT NOT NULL CHECK(quality_gate_state IN (
        'not_run', 'blocking', 'warnings_pending', 'warnings_accepted', 'passed', 'not_assessable')),
    source_evidence_json TEXT NULL,
    metadata_snapshot_json TEXT NULL,
    content_manifest_sha256 TEXT NULL CHECK(content_manifest_sha256 IS NULL OR length(content_manifest_sha256) = 64),
    warning_acknowledged_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    sealed_at_utc TEXT NULL,
    CHECK((lifecycle_state = 'draft' AND sealed_at_utc IS NULL) OR
          (lifecycle_state IN ('sealed', 'retired') AND sealed_at_utc IS NOT NULL)),
    CHECK(parent_version_id IS NULL OR parent_version_id <> dataset_version_id),
    UNIQUE(dataset_id, version_number)
);

CREATE TABLE images (
    image_id TEXT PRIMARY KEY,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    source_file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    normalized_file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    import_source_key TEXT NOT NULL,
    sort_index INTEGER NOT NULL CHECK(sort_index >= 0),
    content_container TEXT NOT NULL CHECK(content_container IN ('jpeg', 'mpo', 'tiff')),
    primary_frame_index INTEGER NULL CHECK(primary_frame_index IS NULL OR primary_frame_index >= 0),
    width INTEGER NULL CHECK(width IS NULL OR width > 0),
    height INTEGER NULL CHECK(height IS NULL OR height > 0),
    capture_time_utc TEXT NULL,
    manufacturer TEXT NULL,
    camera_model TEXT NULL,
    lens_model TEXT NULL,
    image_state TEXT NOT NULL CHECK(image_state IN (
        'imported', 'processing_input', 'duplicate', 'corrupt', 'out_of_scope', 'excluded')),
    metadata_state TEXT NOT NULL CHECK(metadata_state IN (
        'not_parsed', 'parsed', 'missing_required', 'conflict', 'abnormal')),
    duplicate_of_image_id TEXT NULL REFERENCES images(image_id) ON DELETE RESTRICT,
    raw_metadata_json TEXT NULL,
    created_at_utc TEXT NOT NULL,
    CHECK(duplicate_of_image_id IS NULL OR duplicate_of_image_id <> image_id),
    UNIQUE(dataset_version_id, import_source_key),
    UNIQUE(dataset_version_id, sort_index),
    UNIQUE(dataset_version_id, source_file_object_id)
);

CREATE TABLE image_frames (
    image_frame_id TEXT PRIMARY KEY,
    image_id TEXT NOT NULL REFERENCES images(image_id) ON DELETE RESTRICT,
    frame_index INTEGER NOT NULL CHECK(frame_index >= 0),
    frame_role TEXT NOT NULL CHECK(frame_role IN (
        'primary_photogrammetry', 'auxiliary', 'thumbnail', 'unknown')),
    width INTEGER NULL CHECK(width IS NULL OR width > 0),
    height INTEGER NULL CHECK(height IS NULL OR height > 0),
    decode_state TEXT NOT NULL CHECK(decode_state IN ('not_checked', 'decoded', 'failed')),
    normalized_file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    metadata_json TEXT NULL,
    UNIQUE(image_id, frame_index)
);

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
    UNIQUE(image_id, field_name, source_kind)
);

CREATE TABLE processing_jobs (
    processing_job_id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    job_type TEXT NOT NULL CHECK(job_type IN (
        'ingestion_qc', 'photogrammetry', 'tileset_conversion', 'export', 'compatibility_check')),
    requested_outputs_json TEXT NOT NULL,
    parameter_profile TEXT NOT NULL,
    parameter_schema_version TEXT NOT NULL,
    parameters_json TEXT NOT NULL,
    parameter_sha256 TEXT NOT NULL CHECK(length(parameter_sha256) = 64),
    lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN (
        'queued', 'preparing', 'running', 'cancelling', 'cancelled',
        'succeeded', 'partially_failed', 'failed')),
    recovery_state TEXT NOT NULL CHECK(recovery_state IN (
        'not_applicable', 'retry_available', 'checkpoint_available', 'requires_full_execution', 'blocked')),
    priority INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT NOT NULL,
    submitted_at_utc TEXT NOT NULL,
    started_at_utc TEXT NULL,
    ended_at_utc TEXT NULL
);

CREATE TABLE job_executions (
    job_execution_id TEXT PRIMARY KEY,
    processing_job_id TEXT NOT NULL REFERENCES processing_jobs(processing_job_id) ON DELETE RESTRICT,
    attempt_number INTEGER NOT NULL CHECK(attempt_number > 0),
    execution_mode TEXT NOT NULL CHECK(execution_mode IN ('full', 'checkpoint_resume')),
    worker_type TEXT NOT NULL,
    worker_version TEXT NOT NULL,
    engine_name TEXT NULL,
    engine_version TEXT NULL,
    parameter_sha256 TEXT NOT NULL CHECK(length(parameter_sha256) = 64),
    lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN (
        'pending', 'starting', 'running', 'cancelling', 'cancelled', 'succeeded', 'failed', 'lost')),
    process_id INTEGER NULL CHECK(process_id IS NULL OR process_id > 0),
    lease_token_sha256 TEXT NULL CHECK(lease_token_sha256 IS NULL OR length(lease_token_sha256) = 64),
    lease_expires_at_utc TEXT NULL,
    heartbeat_at_utc TEXT NULL,
    checkpoint_file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    checkpoint_compatibility_state TEXT NOT NULL CHECK(checkpoint_compatibility_state IN (
        'not_checked', 'compatible', 'incompatible', 'unavailable')),
    started_at_utc TEXT NULL,
    ended_at_utc TEXT NULL,
    failure_code TEXT NULL,
    failure_message_sanitized TEXT NULL,
    CHECK(execution_mode <> 'checkpoint_resume' OR checkpoint_compatibility_state = 'compatible'),
    UNIQUE(processing_job_id, attempt_number)
);

CREATE TABLE job_events (
    job_event_id TEXT PRIMARY KEY,
    job_execution_id TEXT NOT NULL REFERENCES job_executions(job_execution_id) ON DELETE RESTRICT,
    sequence_number INTEGER NOT NULL CHECK(sequence_number >= 0),
    occurred_at_utc TEXT NOT NULL,
    stage TEXT NOT NULL,
    event_kind TEXT NOT NULL,
    progress_percent REAL NULL CHECK(progress_percent IS NULL OR (progress_percent >= 0 AND progress_percent <= 100)),
    message_sanitized TEXT NULL,
    log_file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    metrics_json TEXT NULL,
    UNIQUE(job_execution_id, sequence_number)
);

CREATE TABLE positioning_aux_files (
    positioning_aux_file_id TEXT PRIMARY KEY,
    dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    auxiliary_type TEXT NOT NULL,
    association_evidence_json TEXT NULL,
    retention_state TEXT NOT NULL CHECK(retention_state IN ('retained', 'quarantined')),
    parse_state TEXT NOT NULL CHECK(parse_state IN ('not_attempted', 'unsupported', 'parsed', 'failed')),
    quality_state TEXT NOT NULL CHECK(quality_state IN ('not_checked', 'passed', 'warning', 'failed')),
    parser_name TEXT NULL,
    parser_version TEXT NULL,
    parsed_summary_json TEXT NULL,
    created_at_utc TEXT NOT NULL,
    UNIQUE(dataset_version_id, file_object_id)
);

CREATE TABLE positioning_aux_usage (
    positioning_aux_usage_id TEXT PRIMARY KEY,
    positioning_aux_file_id TEXT NOT NULL REFERENCES positioning_aux_files(positioning_aux_file_id) ON DELETE RESTRICT,
    job_execution_id TEXT NOT NULL REFERENCES job_executions(job_execution_id) ON DELETE RESTRICT,
    usage_state TEXT NOT NULL CHECK(usage_state IN ('used', 'rejected')),
    evidence_json TEXT NULL,
    recorded_at_utc TEXT NOT NULL,
    UNIQUE(positioning_aux_file_id, job_execution_id)
);

CREATE TABLE result_series (
    result_series_id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    dataset_version_id TEXT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    series_kind TEXT NOT NULL CHECK(series_kind IN (
        'aerotriangulation', 'dom', 'dsm', 'dense_point_cloud', 'textured_mesh',
        'raw_gaussian', 'mesh_3d_tiles', 'cesium_gaussian_3d_tiles',
        'browse_raster', 'browse_point_cloud', 'export_package')),
    name TEXT NOT NULL,
    parent_series_id TEXT NULL REFERENCES result_series(result_series_id) ON DELETE RESTRICT,
    created_at_utc TEXT NOT NULL,
    CHECK(parent_series_id IS NULL OR parent_series_id <> result_series_id)
);

CREATE TABLE results (
    result_id TEXT PRIMARY KEY,
    result_series_id TEXT NOT NULL REFERENCES result_series(result_series_id) ON DELETE RESTRICT,
    version_number INTEGER NOT NULL CHECK(version_number > 0),
    source_dataset_version_id TEXT NOT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    source_processing_job_id TEXT NOT NULL REFERENCES processing_jobs(processing_job_id) ON DELETE RESTRICT,
    source_job_execution_id TEXT NOT NULL REFERENCES job_executions(job_execution_id) ON DELETE RESTRICT,
    source_result_id TEXT NULL REFERENCES results(result_id) ON DELETE RESTRICT,
    result_kind TEXT NOT NULL,
    lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN (
        'candidate', 'validating', 'published', 'rejected', 'superseded', 'deleted')),
    crs_id TEXT NULL REFERENCES crs_definitions(crs_id) ON DELETE RESTRICT,
    vertical_reference TEXT NULL,
    local_origin_json TEXT NULL,
    axis_convention TEXT NULL,
    unit TEXT NULL,
    bounds_json TEXT NULL,
    resolution_or_density_json TEXT NULL,
    engine_version TEXT NULL,
    converter_version TEXT NULL,
    parameter_sha256 TEXT NOT NULL CHECK(length(parameter_sha256) = 64),
    accuracy_level TEXT NOT NULL CHECK(accuracy_level IN (
        'unknown', 'relative_model', 'georeferenced_visualization',
        'configured_survey_candidate', 'validated_survey_grade')),
    created_at_utc TEXT NOT NULL,
    published_at_utc TEXT NULL,
    superseded_by_result_id TEXT NULL REFERENCES results(result_id) ON DELETE RESTRICT,
    CHECK(source_result_id IS NULL OR source_result_id <> result_id),
    CHECK(superseded_by_result_id IS NULL OR superseded_by_result_id <> result_id),
    CHECK((lifecycle_state = 'published' AND published_at_utc IS NOT NULL) OR lifecycle_state <> 'published'),
    UNIQUE(result_series_id, version_number)
);

CREATE TABLE result_files (
    result_file_id TEXT PRIMARY KEY,
    result_id TEXT NOT NULL REFERENCES results(result_id) ON DELETE RESTRICT,
    file_object_id TEXT NOT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    file_role TEXT NOT NULL CHECK(file_role IN (
        'primary', 'sidecar', 'metadata', 'texture', 'tile', 'tileset_json',
        'report', 'preview', 'export_manifest')),
    relative_path TEXT NOT NULL,
    is_required INTEGER NOT NULL CHECK(is_required IN (0, 1)),
    byte_length_snapshot INTEGER NOT NULL CHECK(byte_length_snapshot >= 0),
    content_hash_snapshot TEXT NOT NULL CHECK(length(content_hash_snapshot) = 64),
    CHECK(length(relative_path) > 0 AND substr(relative_path, 1, 1) NOT IN ('/', '\') AND instr(relative_path, ':') = 0),
    UNIQUE(result_id, relative_path)
);

CREATE TABLE result_dependencies (
    result_id TEXT NOT NULL REFERENCES results(result_id) ON DELETE RESTRICT,
    depends_on_result_id TEXT NOT NULL REFERENCES results(result_id) ON DELETE RESTRICT,
    dependency_kind TEXT NOT NULL CHECK(dependency_kind IN (
        'derived_from', 'converted_from', 'validated_against', 'supersedes', 'shares_aerotriangulation')),
    CHECK(result_id <> depends_on_result_id),
    PRIMARY KEY(result_id, depends_on_result_id, dependency_kind)
);

CREATE TABLE quality_reports (
    quality_report_id TEXT PRIMARY KEY,
    report_type TEXT NOT NULL,
    version_number INTEGER NOT NULL CHECK(version_number > 0),
    lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN ('draft', 'final', 'superseded')),
    dataset_version_id TEXT NULL REFERENCES dataset_versions(dataset_version_id) ON DELETE RESTRICT,
    processing_job_id TEXT NULL REFERENCES processing_jobs(processing_job_id) ON DELETE RESTRICT,
    job_execution_id TEXT NULL REFERENCES job_executions(job_execution_id) ON DELETE RESTRICT,
    result_id TEXT NULL REFERENCES results(result_id) ON DELETE RESTRICT,
    created_by_execution_id TEXT NULL REFERENCES job_executions(job_execution_id) ON DELETE RESTRICT,
    report_file_object_id TEXT NULL REFERENCES file_objects(file_object_id) ON DELETE RESTRICT,
    schema_version TEXT NOT NULL,
    summary_severity TEXT NOT NULL CHECK(summary_severity IN ('none', 'info', 'warning', 'blocking', 'error')),
    summary_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    finalized_at_utc TEXT NULL,
    CHECK((dataset_version_id IS NOT NULL) + (processing_job_id IS NOT NULL) +
          (job_execution_id IS NOT NULL) + (result_id IS NOT NULL) = 1),
    CHECK((lifecycle_state = 'draft' AND finalized_at_utc IS NULL) OR
          (lifecycle_state IN ('final', 'superseded') AND finalized_at_utc IS NOT NULL))
);

CREATE TABLE quality_findings (
    quality_finding_id TEXT PRIMARY KEY,
    quality_report_id TEXT NOT NULL REFERENCES quality_reports(quality_report_id) ON DELETE RESTRICT,
    sort_index INTEGER NOT NULL CHECK(sort_index >= 0),
    check_code TEXT NOT NULL,
    severity TEXT NOT NULL CHECK(severity IN ('blocking', 'warning', 'info')),
    conclusion TEXT NOT NULL CHECK(conclusion IN ('passed', 'failed', 'not_assessable', 'not_applicable')),
    affected_entity_type TEXT NULL,
    affected_entity_id TEXT NULL,
    metric_json TEXT NULL,
    threshold_json TEXT NULL,
    recommendation TEXT NULL,
    UNIQUE(quality_report_id, sort_index)
);

CREATE INDEX idx_dataset_versions_dataset ON dataset_versions(dataset_id, version_number);
CREATE INDEX idx_images_dataset_version ON images(dataset_version_id, sort_index);
CREATE INDEX idx_jobs_dataset_state ON processing_jobs(dataset_version_id, lifecycle_state);
CREATE INDEX idx_executions_job_attempt ON job_executions(processing_job_id, attempt_number);
CREATE INDEX idx_positioning_usage_execution ON positioning_aux_usage(job_execution_id, usage_state);
CREATE INDEX idx_results_source_dataset ON results(source_dataset_version_id, lifecycle_state);
CREATE INDEX idx_quality_reports_result ON quality_reports(result_id, lifecycle_state);

CREATE TRIGGER seal_dataset_version_on_job_insert
AFTER INSERT ON processing_jobs
BEGIN
    UPDATE dataset_versions
    SET lifecycle_state = 'sealed', sealed_at_utc = COALESCE(sealed_at_utc, NEW.submitted_at_utc)
    WHERE dataset_version_id = NEW.dataset_version_id AND lifecycle_state = 'draft';
END;

CREATE TRIGGER validate_job_project
BEFORE INSERT ON processing_jobs
WHEN NOT EXISTS (
    SELECT 1 FROM dataset_versions dv
    JOIN datasets d ON d.dataset_id = dv.dataset_id
    WHERE dv.dataset_version_id = NEW.dataset_version_id AND d.project_id = NEW.project_id)
BEGIN
    SELECT RAISE(ABORT, 'processing job project does not own dataset version');
END;

CREATE TRIGGER immutable_sealed_dataset_version_update
BEFORE UPDATE ON dataset_versions
WHEN OLD.sealed_at_utc IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset version is immutable');
END;

CREATE TRIGGER immutable_sealed_dataset_version_delete
BEFORE DELETE ON dataset_versions
WHEN OLD.sealed_at_utc IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset version cannot be deleted');
END;

CREATE TRIGGER immutable_sealed_images_insert
BEFORE INSERT ON images
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id = NEW.dataset_version_id AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_images_update
BEFORE UPDATE ON images
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id IN (OLD.dataset_version_id, NEW.dataset_version_id) AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_images_delete
BEFORE DELETE ON images
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id = OLD.dataset_version_id AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image manifest is immutable');
END;

CREATE TRIGGER immutable_sealed_image_frames_insert
BEFORE INSERT ON image_frames
WHEN EXISTS (
    SELECT 1 FROM images i JOIN dataset_versions dv ON dv.dataset_version_id = i.dataset_version_id
    WHERE i.image_id = NEW.image_id AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image frames are immutable');
END;

CREATE TRIGGER immutable_sealed_image_frames_update
BEFORE UPDATE ON image_frames
WHEN EXISTS (
    SELECT 1 FROM images i JOIN dataset_versions dv ON dv.dataset_version_id = i.dataset_version_id
    WHERE i.image_id IN (OLD.image_id, NEW.image_id) AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image frames are immutable');
END;

CREATE TRIGGER immutable_sealed_image_frames_delete
BEFORE DELETE ON image_frames
WHEN EXISTS (
    SELECT 1 FROM images i JOIN dataset_versions dv ON dv.dataset_version_id = i.dataset_version_id
    WHERE i.image_id = OLD.image_id AND dv.sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset image frames are immutable');
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

CREATE TRIGGER immutable_sealed_positioning_delete
BEFORE DELETE ON positioning_aux_files
WHEN EXISTS (SELECT 1 FROM dataset_versions WHERE dataset_version_id = OLD.dataset_version_id AND sealed_at_utc IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'sealed dataset positioning manifest is immutable');
END;

CREATE TRIGGER immutable_available_file_identity
BEFORE UPDATE ON file_objects
WHEN OLD.storage_state = 'available' AND (
    NEW.object_kind IS NOT OLD.object_kind OR NEW.hash_algorithm IS NOT OLD.hash_algorithm OR
    NEW.content_hash IS NOT OLD.content_hash OR NEW.byte_length IS NOT OLD.byte_length OR
    NEW.media_type IS NOT OLD.media_type OR NEW.object_key IS NOT OLD.object_key)
BEGIN
    SELECT RAISE(ABORT, 'available file object identity is immutable');
END;

CREATE TRIGGER immutable_processing_job_identity
BEFORE UPDATE ON processing_jobs
WHEN NEW.project_id IS NOT OLD.project_id OR NEW.dataset_version_id IS NOT OLD.dataset_version_id OR
     NEW.job_type IS NOT OLD.job_type OR NEW.requested_outputs_json IS NOT OLD.requested_outputs_json OR
     NEW.parameter_profile IS NOT OLD.parameter_profile OR
     NEW.parameter_schema_version IS NOT OLD.parameter_schema_version OR
     NEW.parameters_json IS NOT OLD.parameters_json OR NEW.parameter_sha256 IS NOT OLD.parameter_sha256 OR
     NEW.created_at_utc IS NOT OLD.created_at_utc OR NEW.submitted_at_utc IS NOT OLD.submitted_at_utc
BEGIN
    SELECT RAISE(ABORT, 'processing job input and parameters are immutable');
END;

CREATE TRIGGER immutable_job_execution_identity
BEFORE UPDATE ON job_executions
WHEN NEW.processing_job_id IS NOT OLD.processing_job_id OR NEW.attempt_number IS NOT OLD.attempt_number OR
     NEW.execution_mode IS NOT OLD.execution_mode OR NEW.worker_type IS NOT OLD.worker_type OR
     NEW.worker_version IS NOT OLD.worker_version OR NEW.engine_name IS NOT OLD.engine_name OR
     NEW.engine_version IS NOT OLD.engine_version OR NEW.parameter_sha256 IS NOT OLD.parameter_sha256 OR
     NEW.checkpoint_file_object_id IS NOT OLD.checkpoint_file_object_id OR
     NEW.checkpoint_compatibility_state IS NOT OLD.checkpoint_compatibility_state
BEGIN
    SELECT RAISE(ABORT, 'job execution identity is immutable');
END;

CREATE TRIGGER validate_positioning_usage_execution
BEFORE INSERT ON positioning_aux_usage
WHEN NOT EXISTS (
    SELECT 1 FROM positioning_aux_files p
    JOIN job_executions e ON e.job_execution_id = NEW.job_execution_id
    JOIN processing_jobs j ON j.processing_job_id = e.processing_job_id
    WHERE p.positioning_aux_file_id = NEW.positioning_aux_file_id
      AND p.dataset_version_id = j.dataset_version_id)
BEGIN
    SELECT RAISE(ABORT, 'positioning usage execution does not use the same dataset version');
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

CREATE TRIGGER validate_result_lineage
BEFORE INSERT ON results
WHEN NOT EXISTS (
    SELECT 1 FROM job_executions e
    JOIN processing_jobs j ON j.processing_job_id = e.processing_job_id
    JOIN result_series s ON s.result_series_id = NEW.result_series_id
    WHERE e.job_execution_id = NEW.source_job_execution_id
      AND j.processing_job_id = NEW.source_processing_job_id
      AND j.dataset_version_id = NEW.source_dataset_version_id
      AND s.project_id = j.project_id
      AND (s.dataset_version_id IS NULL OR s.dataset_version_id = NEW.source_dataset_version_id)
      AND s.series_kind = NEW.result_kind)
BEGIN
    SELECT RAISE(ABORT, 'result source execution, job, and dataset version do not match');
END;

CREATE TRIGGER validate_result_file_snapshot
BEFORE INSERT ON result_files
WHEN NOT EXISTS (
    SELECT 1 FROM file_objects f
    WHERE f.file_object_id = NEW.file_object_id AND f.storage_state = 'available'
      AND f.byte_length = NEW.byte_length_snapshot AND f.content_hash = NEW.content_hash_snapshot)
BEGIN
    SELECT RAISE(ABORT, 'result file snapshot does not match an available file object');
END;

CREATE TRIGGER validate_result_file_snapshot_update
BEFORE UPDATE ON result_files
WHEN NOT EXISTS (
    SELECT 1 FROM file_objects f
    WHERE f.file_object_id = NEW.file_object_id AND f.storage_state = 'available'
      AND f.byte_length = NEW.byte_length_snapshot AND f.content_hash = NEW.content_hash_snapshot)
BEGIN
    SELECT RAISE(ABORT, 'result file snapshot does not match an available file object');
END;

CREATE TRIGGER immutable_result_lineage
BEFORE UPDATE ON results
WHEN NEW.result_series_id IS NOT OLD.result_series_id OR NEW.version_number IS NOT OLD.version_number OR
     NEW.source_dataset_version_id IS NOT OLD.source_dataset_version_id OR
     NEW.source_processing_job_id IS NOT OLD.source_processing_job_id OR
     NEW.source_job_execution_id IS NOT OLD.source_job_execution_id OR
     NEW.source_result_id IS NOT OLD.source_result_id OR NEW.result_kind IS NOT OLD.result_kind OR
     NEW.parameter_sha256 IS NOT OLD.parameter_sha256 OR NEW.created_at_utc IS NOT OLD.created_at_utc
BEGIN
    SELECT RAISE(ABORT, 'result lineage is immutable');
END;

CREATE TRIGGER immutable_result_series_identity
BEFORE UPDATE ON result_series
WHEN EXISTS (SELECT 1 FROM results WHERE result_series_id = OLD.result_series_id) AND (
    NEW.project_id IS NOT OLD.project_id OR NEW.dataset_version_id IS NOT OLD.dataset_version_id OR
    NEW.series_kind IS NOT OLD.series_kind OR NEW.parent_series_id IS NOT OLD.parent_series_id OR
    NEW.created_at_utc IS NOT OLD.created_at_utc)
BEGIN
    SELECT RAISE(ABORT, 'result series identity is immutable after its first result');
END;

CREATE TRIGGER require_result_evidence_before_publish
BEFORE UPDATE ON results
WHEN NEW.lifecycle_state = 'published' AND OLD.lifecycle_state <> 'published' AND (
    NOT EXISTS (SELECT 1 FROM result_files WHERE result_id = NEW.result_id) OR
    NOT EXISTS (SELECT 1 FROM quality_reports WHERE result_id = NEW.result_id AND lifecycle_state = 'final'))
BEGIN
    SELECT RAISE(ABORT, 'result requires files and a final quality report before publication');
END;

CREATE TRIGGER reject_direct_published_result_insert
BEFORE INSERT ON results
WHEN NEW.lifecycle_state = 'published'
BEGIN
    SELECT RAISE(ABORT, 'result must be validated before publication');
END;

CREATE TRIGGER immutable_published_result_identity
BEFORE UPDATE ON results
WHEN OLD.lifecycle_state IN ('published', 'superseded') AND (
    NEW.result_series_id IS NOT OLD.result_series_id OR NEW.version_number IS NOT OLD.version_number OR
    NEW.source_dataset_version_id IS NOT OLD.source_dataset_version_id OR
    NEW.source_processing_job_id IS NOT OLD.source_processing_job_id OR
    NEW.source_job_execution_id IS NOT OLD.source_job_execution_id OR
    NEW.source_result_id IS NOT OLD.source_result_id OR NEW.result_kind IS NOT OLD.result_kind OR
    NEW.crs_id IS NOT OLD.crs_id OR NEW.vertical_reference IS NOT OLD.vertical_reference OR
    NEW.local_origin_json IS NOT OLD.local_origin_json OR NEW.axis_convention IS NOT OLD.axis_convention OR
    NEW.unit IS NOT OLD.unit OR NEW.bounds_json IS NOT OLD.bounds_json OR
    NEW.resolution_or_density_json IS NOT OLD.resolution_or_density_json OR
    NEW.engine_version IS NOT OLD.engine_version OR NEW.converter_version IS NOT OLD.converter_version OR
    NEW.parameter_sha256 IS NOT OLD.parameter_sha256 OR NEW.accuracy_level IS NOT OLD.accuracy_level OR
    NEW.published_at_utc IS NOT OLD.published_at_utc)
BEGIN
    SELECT RAISE(ABORT, 'published result identity is immutable');
END;

CREATE TRIGGER immutable_published_result_lifecycle
BEFORE UPDATE ON results
WHEN OLD.lifecycle_state IN ('published', 'superseded') AND NOT (
    NEW.lifecycle_state = OLD.lifecycle_state OR
    (OLD.lifecycle_state = 'published' AND NEW.lifecycle_state = 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'published result lifecycle can only advance to superseded');
END;

CREATE TRIGGER immutable_published_result_delete
BEFORE DELETE ON results
WHEN OLD.lifecycle_state IN ('published', 'superseded')
BEGIN
    SELECT RAISE(ABORT, 'published result cannot be deleted');
END;

CREATE TRIGGER immutable_published_result_files_insert
BEFORE INSERT ON result_files
WHEN EXISTS (SELECT 1 FROM results WHERE result_id = NEW.result_id AND lifecycle_state IN ('published', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'published result files are immutable');
END;

CREATE TRIGGER immutable_published_result_files_update
BEFORE UPDATE ON result_files
WHEN EXISTS (SELECT 1 FROM results WHERE result_id IN (OLD.result_id, NEW.result_id) AND lifecycle_state IN ('published', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'published result files are immutable');
END;

CREATE TRIGGER immutable_published_result_files_delete
BEFORE DELETE ON result_files
WHEN EXISTS (SELECT 1 FROM results WHERE result_id = OLD.result_id AND lifecycle_state IN ('published', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'published result files are immutable');
END;

CREATE TRIGGER immutable_published_result_dependencies_insert
BEFORE INSERT ON result_dependencies
WHEN EXISTS (SELECT 1 FROM results WHERE result_id = NEW.result_id AND lifecycle_state IN ('published', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'published result dependencies are immutable');
END;

CREATE TRIGGER immutable_published_result_dependencies_update
BEFORE UPDATE ON result_dependencies
WHEN EXISTS (SELECT 1 FROM results WHERE result_id IN (OLD.result_id, NEW.result_id) AND lifecycle_state IN ('published', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'published result dependencies are immutable');
END;

CREATE TRIGGER immutable_published_result_dependencies_delete
BEFORE DELETE ON result_dependencies
WHEN EXISTS (SELECT 1 FROM results WHERE result_id = OLD.result_id AND lifecycle_state IN ('published', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'published result dependencies are immutable');
END;

CREATE TRIGGER immutable_final_quality_report
BEFORE UPDATE ON quality_reports
WHEN OLD.lifecycle_state IN ('final', 'superseded') AND (
    NEW.report_type IS NOT OLD.report_type OR NEW.version_number IS NOT OLD.version_number OR
    NEW.dataset_version_id IS NOT OLD.dataset_version_id OR NEW.processing_job_id IS NOT OLD.processing_job_id OR
    NEW.job_execution_id IS NOT OLD.job_execution_id OR NEW.result_id IS NOT OLD.result_id OR
    NEW.created_by_execution_id IS NOT OLD.created_by_execution_id OR
    NEW.report_file_object_id IS NOT OLD.report_file_object_id OR NEW.schema_version IS NOT OLD.schema_version OR
    NEW.summary_severity IS NOT OLD.summary_severity OR NEW.summary_json IS NOT OLD.summary_json OR
    NEW.created_at_utc IS NOT OLD.created_at_utc OR NEW.finalized_at_utc IS NOT OLD.finalized_at_utc)
BEGIN
    SELECT RAISE(ABORT, 'final quality report is immutable');
END;

CREATE TRIGGER immutable_final_quality_report_lifecycle
BEFORE UPDATE ON quality_reports
WHEN OLD.lifecycle_state IN ('final', 'superseded') AND NOT (
    NEW.lifecycle_state = OLD.lifecycle_state OR
    (OLD.lifecycle_state = 'final' AND NEW.lifecycle_state = 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'final quality report lifecycle can only advance to superseded');
END;

CREATE TRIGGER immutable_final_quality_report_delete
BEFORE DELETE ON quality_reports
WHEN OLD.lifecycle_state IN ('final', 'superseded')
BEGIN
    SELECT RAISE(ABORT, 'final quality report cannot be deleted');
END;

CREATE TRIGGER immutable_final_quality_findings_insert
BEFORE INSERT ON quality_findings
WHEN EXISTS (SELECT 1 FROM quality_reports WHERE quality_report_id = NEW.quality_report_id AND lifecycle_state IN ('final', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'final quality report findings are immutable');
END;

CREATE TRIGGER immutable_final_quality_findings_update
BEFORE UPDATE ON quality_findings
WHEN EXISTS (SELECT 1 FROM quality_reports WHERE quality_report_id IN (OLD.quality_report_id, NEW.quality_report_id) AND lifecycle_state IN ('final', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'final quality report findings are immutable');
END;

CREATE TRIGGER immutable_final_quality_findings_delete
BEFORE DELETE ON quality_findings
WHEN EXISTS (SELECT 1 FROM quality_reports WHERE quality_report_id = OLD.quality_report_id AND lifecycle_state IN ('final', 'superseded'))
BEGIN
    SELECT RAISE(ABORT, 'final quality report findings are immutable');
END;
