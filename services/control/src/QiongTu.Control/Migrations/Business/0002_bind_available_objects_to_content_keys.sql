CREATE TABLE migration_0002_available_object_guard (
    invalid_row_count INTEGER NOT NULL CHECK(invalid_row_count = 0)
);

INSERT INTO migration_0002_available_object_guard(invalid_row_count)
SELECT count(*)
FROM file_objects
WHERE storage_state = 'available' AND (
    object_key IS NULL OR
    object_key <> 'sha256/' || substr(content_hash, 1, 2) || '/' || content_hash);

DROP TABLE migration_0002_available_object_guard;

CREATE TRIGGER file_object_available_requires_content_key_insert
BEFORE INSERT ON file_objects
WHEN NEW.storage_state = 'available' AND (
    NEW.object_key IS NULL OR
    NEW.object_key <> 'sha256/' || substr(NEW.content_hash, 1, 2) || '/' || NEW.content_hash)
BEGIN
    SELECT RAISE(ABORT, 'available file object key must match its SHA-256 content address');
END;

CREATE TRIGGER file_object_available_requires_content_key_update
BEFORE UPDATE ON file_objects
WHEN NEW.storage_state = 'available' AND (
    NEW.object_key IS NULL OR
    NEW.object_key <> 'sha256/' || substr(NEW.content_hash, 1, 2) || '/' || NEW.content_hash)
BEGIN
    SELECT RAISE(ABORT, 'available file object key must match its SHA-256 content address');
END;
