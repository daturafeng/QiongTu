ALTER TABLE image_inspection_runs
ADD COLUMN support_disposition TEXT NULL CHECK(support_disposition IS NULL OR support_disposition IN (
    'unsupported_vendor_payload', 'image_not_processable'));

ALTER TABLE image_inspection_runs
ADD COLUMN support_policy_version TEXT NULL CHECK(support_policy_version IS NULL OR support_policy_version = 'vendor-payload-support.v1');

DROP TRIGGER validate_image_inspection_transition;

UPDATE image_inspection_runs
SET support_disposition = CASE failure_code
        WHEN 'mpf_unreferenced_trailing_data' THEN 'unsupported_vendor_payload'
        ELSE 'image_not_processable'
    END,
    support_policy_version = 'vendor-payload-support.v1'
WHERE status = 'blocked';

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

CREATE TRIGGER validate_image_inspection_support_disposition_insert
BEFORE INSERT ON image_inspection_runs
WHEN (NEW.status = 'blocked' AND (NEW.support_disposition IS NULL OR NEW.support_policy_version IS NULL))
   OR (NEW.status <> 'blocked' AND (NEW.support_disposition IS NOT NULL OR NEW.support_policy_version IS NOT NULL))
   OR (NEW.status = 'blocked' AND (
        NEW.support_policy_version IS NOT 'vendor-payload-support.v1'
        OR NEW.support_disposition IS NOT CASE NEW.failure_code
            WHEN 'mpf_unreferenced_trailing_data' THEN 'unsupported_vendor_payload'
            ELSE 'image_not_processable'
        END))
BEGIN
    SELECT RAISE(ABORT, 'image inspection support disposition must match blocked state');
END;

CREATE TRIGGER validate_image_inspection_support_disposition_update
BEFORE UPDATE ON image_inspection_runs
WHEN (NEW.status = 'blocked' AND (NEW.support_disposition IS NULL OR NEW.support_policy_version IS NULL))
   OR (NEW.status <> 'blocked' AND (NEW.support_disposition IS NOT NULL OR NEW.support_policy_version IS NOT NULL))
   OR (NEW.status = 'blocked' AND (
        NEW.support_policy_version IS NOT 'vendor-payload-support.v1'
        OR NEW.support_disposition IS NOT CASE NEW.failure_code
            WHEN 'mpf_unreferenced_trailing_data' THEN 'unsupported_vendor_payload'
            ELSE 'image_not_processable'
        END))
BEGIN
    SELECT RAISE(ABORT, 'image inspection support disposition must match blocked state');
END;
