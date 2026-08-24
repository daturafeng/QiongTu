ALTER TABLE crs_definitions ADD COLUMN crs_type TEXT NULL;
ALTER TABLE crs_definitions ADD COLUMN captured_at_utc TEXT NULL;

CREATE TABLE migration_0003_crs_identity_guard (
    invalid_row_count INTEGER NOT NULL CHECK(invalid_row_count = 0)
);

INSERT INTO migration_0003_crs_identity_guard(invalid_row_count)
SELECT count(*)
FROM (
    SELECT 1
    FROM crs_definitions
    GROUP BY
        COALESCE(authority, ''),
        COALESCE(code, ''),
        COALESCE(wkt, ''),
        COALESCE(projjson, ''),
        COALESCE(crs_type, ''),
        horizontal_unit,
        COALESCE(vertical_reference, ''),
        axis_order
    HAVING count(*) > 1
);

DROP TABLE migration_0003_crs_identity_guard;

CREATE TABLE migration_0003_authority_crs_guard (
    invalid_row_count INTEGER NOT NULL CHECK(invalid_row_count = 0)
);

INSERT INTO migration_0003_authority_crs_guard(invalid_row_count)
SELECT count(*)
FROM (
    SELECT 1
    FROM crs_definitions
    WHERE authority IS NOT NULL
    GROUP BY authority, code, COALESCE(vertical_reference, '')
    HAVING count(*) > 1
);

DROP TABLE migration_0003_authority_crs_guard;

CREATE UNIQUE INDEX ux_crs_definitions_identity_expr ON crs_definitions(
    COALESCE(authority, ''),
    COALESCE(code, ''),
    COALESCE(wkt, ''),
    COALESCE(projjson, ''),
    COALESCE(crs_type, ''),
    horizontal_unit,
    COALESCE(vertical_reference, ''),
    axis_order
);

CREATE UNIQUE INDEX ux_crs_definitions_authority_identity_expr ON crs_definitions(
    authority,
    code,
    COALESCE(vertical_reference, '')
) WHERE authority IS NOT NULL;

CREATE TABLE catalog_mutations (
    request_id TEXT PRIMARY KEY CHECK(length(request_id) > 0 AND length(request_id) <= 128),
    method TEXT NOT NULL CHECK(length(method) > 0 AND length(method) <= 128),
    parameters_sha256 TEXT NOT NULL CHECK(length(parameters_sha256) = 64 AND parameters_sha256 NOT GLOB '*[^0-9a-f]*'),
    response_json TEXT NOT NULL CHECK(length(response_json) > 0 AND length(response_json) <= 65536),
    completed_at_utc TEXT NOT NULL
);
