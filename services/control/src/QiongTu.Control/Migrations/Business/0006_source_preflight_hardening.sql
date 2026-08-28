CREATE TRIGGER validate_source_preflight_item_transition
BEFORE UPDATE OF status ON source_preflight_items
WHEN NEW.status IS NOT OLD.status AND NOT (
    (OLD.status = 'queued' AND NEW.status = 'running') OR
    (OLD.status = 'running' AND NEW.status IN ('queued', 'completed', 'failed')))
BEGIN
    SELECT RAISE(ABORT, 'invalid source preflight item transition');
END;

DROP TRIGGER validate_source_preflight_run_update;

CREATE TRIGGER validate_source_preflight_run_update
BEFORE UPDATE ON source_preflight_runs
WHEN NEW.import_session_id IS NOT OLD.import_session_id
   OR NEW.dataset_version_id IS NOT OLD.dataset_version_id
   OR NEW.parser_profile IS NOT OLD.parser_profile
   OR NEW.parser_version IS NOT OLD.parser_version
   OR NEW.policy_version IS NOT OLD.policy_version
   OR ((NEW.source_root_key_snapshot IS NOT OLD.source_root_key_snapshot
        OR NEW.source_locator_manifest_id_snapshot IS NOT OLD.source_locator_manifest_id_snapshot)
       AND NOT (OLD.status = 'interrupted' AND NEW.status = 'interrupted'))
   OR NOT EXISTS (
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
    SELECT RAISE(ABORT, 'source preflight identity requires a verified waiting source binding');
END;

CREATE TRIGGER validate_dataset_source_eligibility_provenance_update
BEFORE UPDATE OF source_eligibility_state, source_evidence_json,
                 source_eligibility_run_id, source_eligibility_decided_at_utc
ON dataset_versions
WHEN (NEW.source_eligibility_state = 'pending' AND
      (NEW.source_eligibility_run_id IS NOT NULL OR
       NEW.source_eligibility_decided_at_utc IS NOT NULL))
   OR (NEW.source_eligibility_state <> 'pending' AND NOT EXISTS (
        SELECT 1
        FROM source_preflight_runs r
        WHERE r.source_preflight_run_id = NEW.source_eligibility_run_id
          AND r.dataset_version_id = NEW.dataset_version_id
          AND r.status = 'completed'
          AND r.decision = NEW.source_eligibility_state
          AND r.evidence_summary_json = NEW.source_evidence_json
          AND NEW.source_eligibility_decided_at_utc IS NOT NULL))
BEGIN
    SELECT RAISE(ABORT, 'dataset source eligibility requires matching completed preflight provenance');
END;
