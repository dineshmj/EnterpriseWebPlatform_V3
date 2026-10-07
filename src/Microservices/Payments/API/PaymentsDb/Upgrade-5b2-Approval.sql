-- =============================================================================
-- EwpPaymentsDb - upgrade for step 5b-2: the payments officer's approval decision
-- =============================================================================
-- For a database created in step 5a / 5b-1, when its payments must be kept (e.g. one
-- waiting for approval). Safe to re-run. A fresh database gets the same columns from
-- EwpPaymentsDb.sql instead.
--   psql -h localhost -U postgres -d EwpPaymentsDb -v ON_ERROR_STOP=1 -f Upgrade-5b2-Approval.sql
-- =============================================================================

ALTER TABLE payments ADD COLUMN IF NOT EXISTS decision_by_user_id VARCHAR(200) NULL;
ALTER TABLE payments ADD COLUMN IF NOT EXISTS decision_at TIMESTAMPTZ NULL;
ALTER TABLE payments ADD COLUMN IF NOT EXISTS decision_remarks VARCHAR(1000) NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payments_decision') THEN
        ALTER TABLE payments ADD CONSTRAINT ck_payments_decision
            CHECK ((decision_by_user_id IS NULL) = (decision_at IS NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payments_decider_not_initiator') THEN
        ALTER TABLE payments ADD CONSTRAINT ck_payments_decider_not_initiator
            CHECK (decision_by_user_id IS NULL OR decision_by_user_id <> initiated_by_user_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payments_decision_only_when_required') THEN
        ALTER TABLE payments ADD CONSTRAINT ck_payments_decision_only_when_required
            CHECK (decision_by_user_id IS NULL OR approval_required);
    END IF;
END $$;

SELECT payment_number, status, amount, approval_required FROM payments ORDER BY id;