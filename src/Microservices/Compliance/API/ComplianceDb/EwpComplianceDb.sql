-- =============================================================================
-- EwpComplianceDb - Compliance bounded context
-- =============================================================================
-- Run while connected to EwpComplianceDb (it drops and recreates the tables):
--   psql -h localhost -U postgres -d EwpComplianceDb -v ON_ERROR_STOP=1 -f EwpComplianceDb.sql
-- The CHECK constraints mirror the ComplianceCase aggregate's invariants.
-- =============================================================================

DROP TABLE IF EXISTS compliance_cases CASCADE;
DROP TABLE IF EXISTS outbox_messages CASCADE;
DROP TABLE IF EXISTS inbox_messages CASCADE;
DROP TABLE IF EXISTS staff_members CASCADE;

CREATE TABLE compliance_cases (
    id BIGSERIAL PRIMARY KEY,

    -- The onboarding application under review (owned by Customer Onboarding,
    -- referenced by value: never-repeating GUID + business number). One compliance
    -- case per application: also the business idempotency key for case creation.
    application_ref UUID NOT NULL,
    application_number VARCHAR(30) NOT NULL,
    customer_number VARCHAR(100) NOT NULL,

    -- The applicant AS KYC VERIFIED THEM (snapshot from kyc.case.approved): what is
    -- screened and what the officer sees. Never refreshed; no contact details.
    applicant_first_name VARCHAR(100) NOT NULL,
    applicant_last_name VARCHAR(100) NOT NULL,
    applicant_address_line1 VARCHAR(200) NULL,
    applicant_address_line2 VARCHAR(200) NULL,
    applicant_city VARCHAR(100) NULL,
    applicant_state VARCHAR(100) NULL,
    applicant_postal_code VARCHAR(20) NULL,
    applicant_country_code CHAR(2) NULL,

    -- The KYC case that approved the application (Customer KYC, by value).
    kyc_case_id BIGINT NOT NULL,

    -- ABAC: officers see and decide only their own branch's cases.
    branch_code VARCHAR(20) NOT NULL,

    -- Separation of Duties: none of these people may take or decide this case.
    initiated_by_user_id VARCHAR(200) NULL,
    kyc_identity_decided_by_user_id VARCHAR(200) NULL,
    kyc_document_decided_by_user_id VARCHAR(200) NULL,

    status VARCHAR(30) NOT NULL,

    -- Screening by the external AML / sanctions / PEP provider. A provider failure
    -- is a technical failure: the case stays SCREENING and is retried; it is never
    -- treated as a clear result.
    screening_outcome VARCHAR(30) NULL,
    screening_provider VARCHAR(100) NULL,
    screening_reference VARCHAR(100) NULL,
    screened_at TIMESTAMPTZ NULL,
    screening_attempts INTEGER NOT NULL DEFAULT 0,
    next_screening_at TIMESTAMPTZ NULL,
    last_screening_error TEXT NULL,

    -- Risk derived from the screening; ABAC: approving needs this clearance level.
    risk_rating VARCHAR(10) NULL,
    required_clearance INTEGER NULL,

    -- ReBAC: the officer the case is assigned to (NULL = shared work queue).
    assigned_officer_user_id VARCHAR(200) NULL,

    hold_reason VARCHAR(4000) NULL,

    decision_by_user_id VARCHAR(200) NULL,
    decision_at TIMESTAMPTZ NULL,
    decision_remarks VARCHAR(4000) NULL,

    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    version BIGINT NOT NULL,

    CONSTRAINT uq_compliance_cases_application_ref UNIQUE (application_ref),

    CONSTRAINT ck_compliance_cases_status
        CHECK (status IN ('SCREENING', 'UNDER_REVIEW', 'ON_HOLD', 'APPROVED', 'REJECTED')),

    CONSTRAINT ck_compliance_cases_screening_outcome
        CHECK (screening_outcome IS NULL OR screening_outcome IN ('CLEAR', 'POTENTIAL_MATCH', 'MATCH')),

    CONSTRAINT ck_compliance_cases_risk_rating
        CHECK (risk_rating IS NULL OR risk_rating IN ('LOW', 'MEDIUM', 'HIGH')),

    -- Out of SCREENING only with a screening result and a risk rating.
    CONSTRAINT ck_compliance_cases_screened
        CHECK (status = 'SCREENING'
               OR (screening_outcome IS NOT NULL AND risk_rating IS NOT NULL AND required_clearance IS NOT NULL)),

    CONSTRAINT ck_compliance_cases_required_clearance
        CHECK (required_clearance IS NULL OR required_clearance BETWEEN 1 AND 9),

    -- Decision metadata exists exactly when the case is final.
    CONSTRAINT ck_compliance_cases_decision
        CHECK ((status IN ('APPROVED', 'REJECTED')) = (decision_by_user_id IS NOT NULL AND decision_at IS NOT NULL)),

    -- A rejection always carries remarks.
    CONSTRAINT ck_compliance_cases_rejection_remarks
        CHECK (status <> 'REJECTED' OR decision_remarks IS NOT NULL),

    CONSTRAINT ck_compliance_cases_hold_reason
        CHECK (status <> 'ON_HOLD' OR hold_reason IS NOT NULL),

    CONSTRAINT ck_compliance_cases_version CHECK (version > 0)
);

-- Work queue: a branch's open cases, oldest first.
CREATE INDEX ix_compliance_cases_branch_status ON compliance_cases (branch_code, status, created_at);
-- Screening worker: cases due for (re)screening.
CREATE INDEX ix_compliance_cases_screening_due ON compliance_cases (next_screening_at) WHERE status = 'SCREENING';
CREATE INDEX ix_compliance_cases_customer_number ON compliance_cases (customer_number);

-- Transactional Outbox (same shape as the other contexts).
CREATE TABLE outbox_messages (
    id UUID PRIMARY KEY,
    sequence BIGINT GENERATED ALWAYS AS IDENTITY,
    aggregate_type VARCHAR(100) NOT NULL,
    aggregate_id VARCHAR(100) NOT NULL,
    event_type VARCHAR(200) NOT NULL,
    payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    workflow_id UUID NULL,
    correlation_id UUID NULL,
    causation_id UUID NULL,
    initiated_by_user_id VARCHAR(200) NULL,
    acted_by_user_id VARCHAR(200) NULL,
    trace_parent VARCHAR(55) NULL,
    published_at TIMESTAMPTZ NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_attempt_at TIMESTAMPTZ NULL,
    last_error TEXT NULL
);

CREATE UNIQUE INDEX ux_compliance_outbox_sequence ON outbox_messages (sequence);
CREATE INDEX ix_compliance_outbox_unpublished ON outbox_messages (sequence) WHERE published_at IS NULL;
CREATE INDEX ix_compliance_outbox_workflow_id ON outbox_messages (workflow_id);
CREATE INDEX ix_compliance_outbox_causation_id ON outbox_messages (causation_id);

-- Staff directory: the LAN ID of each staff member this context has seen (from the
-- officer's token, or from an event that names them). Screens and published events
-- show the LAN ID; records and every rule keep the subject ID (user_id).
CREATE TABLE staff_members (
    user_id VARCHAR(200) PRIMARY KEY,
    lan_id VARCHAR(20) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

-- Inbox (idempotent consumer).
CREATE TABLE inbox_messages (
    id UUID NOT NULL,
    message_id UUID NOT NULL,
    consumer VARCHAR(200) NOT NULL,
    received_at TIMESTAMPTZ NOT NULL,
    processed_at TIMESTAMPTZ NULL,
    CONSTRAINT pk_compliance_inbox_messages PRIMARY KEY (id),
    CONSTRAINT uq_compliance_inbox_messages_message_consumer UNIQUE (message_id, consumer)
);

-- The service's own least-privilege user (created by db/EwpServiceDbUsers.sql).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ewp_compliance_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON compliance_cases, outbox_messages, inbox_messages, staff_members TO ewp_compliance_api;
        GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_compliance_api;
    ELSE
        RAISE WARNING 'Role ewp_compliance_api does not exist yet: run db/EwpServiceDbUsers.sql, then this script again.';
    END IF;
END $$;