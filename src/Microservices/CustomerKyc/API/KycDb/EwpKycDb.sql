DROP TABLE IF EXISTS kyc_cases CASCADE;
DROP TABLE IF EXISTS outbox_messages CASCADE;
DROP TABLE IF EXISTS inbox_messages CASCADE;

CREATE TABLE IF NOT EXISTS kyc_cases (
    id BIGSERIAL PRIMARY KEY,

    -- One KYC case per onboarding APPLICATION (owned by Customer Onboarding and
    -- referenced here by value only - no cross-database foreign key). The
    -- application is identified by Customer Onboarding's ApplicationRef, a GUID
    -- that never repeats (database IDs restart when a database is recreated).
    -- It is also the business idempotency key for case creation.
    application_ref UUID NOT NULL,
    application_number VARCHAR(30) NOT NULL,

    -- ABAC: the branch the application was opened in. Officers see and decide
    -- only the cases of their own branch.
    branch_code VARCHAR(20) NOT NULL,

    -- ReBAC: the officer the case is assigned to ("assigned_to"). Set when an
    -- officer claims the case or makes the first decision; only the assignee
    -- may decide. NULL = in the shared work queue.
    assigned_officer_user_id VARCHAR(200) NULL,

    -- The customer the application belongs to; a customer can have several
    -- applications and therefore several KYC cases over time.
    customer_number VARCHAR(100) NOT NULL,

    -- Overall KYC case status. This becomes APPROVED only after
    -- all mandatory verification stages have been approved.
    status VARCHAR(50) NOT NULL,

    initiated_by_user_id VARCHAR(200) NULL,

    -- Identity Verification workflow stage.
    identity_verification_status VARCHAR(50) NOT NULL DEFAULT 'PENDING_REVIEW',
    identity_verification_by_user_id VARCHAR(200) NULL,
    identity_verification_at TIMESTAMPTZ NULL,
    identity_verification_remarks TEXT NULL,

    -- Document Verification workflow stage.
    document_verification_status VARCHAR(50) NOT NULL DEFAULT 'PENDING_REVIEW',
    document_verification_by_user_id VARCHAR(200) NULL,
    document_verification_at TIMESTAMPTZ NULL,
    document_verification_remarks TEXT NULL,

    -- Overall/final KYC decision. These are populated only when the
    -- overall case reaches APPROVED or REJECTED.
    decision_by_user_id VARCHAR(200) NULL,
    decision_at TIMESTAMPTZ NULL,
    decision_remarks TEXT NULL,

    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,

    -- Optimistic concurrency token of the KycCase aggregate; incremented by every change.
    version BIGINT NOT NULL DEFAULT 1,

    CONSTRAINT uq_kyc_cases_application_ref UNIQUE (application_ref),

    -- A decided stage always has an assigned officer.
    CONSTRAINT ck_kyc_cases_assignment
        CHECK (
            assigned_officer_user_id IS NOT NULL
            OR (
                identity_verification_status = 'PENDING_REVIEW'
                AND document_verification_status = 'PENDING_REVIEW'
            )
        ),

    CONSTRAINT ck_kyc_cases_status
        CHECK (status IN ('PENDING_REVIEW','APPROVED','REJECTED')),

    CONSTRAINT ck_kyc_identity_verification_status
        CHECK (
            identity_verification_status
            IN ('PENDING_REVIEW','APPROVED','REJECTED')
        ),

    CONSTRAINT ck_kyc_document_verification_status
        CHECK (
            document_verification_status
            IN ('PENDING_REVIEW','APPROVED','REJECTED')
        ),

    -- A stage decision actor/time must exist once that stage is completed.
    CONSTRAINT ck_kyc_identity_verification_metadata
        CHECK (
            (
                identity_verification_status = 'PENDING_REVIEW'
                AND identity_verification_by_user_id IS NULL
                AND identity_verification_at IS NULL
            )
            OR
            (
                identity_verification_status IN ('APPROVED','REJECTED')
                AND identity_verification_by_user_id IS NOT NULL
                AND identity_verification_at IS NOT NULL
            )
        ),

    CONSTRAINT ck_kyc_document_verification_metadata
        CHECK (
            (
                document_verification_status = 'PENDING_REVIEW'
                AND document_verification_by_user_id IS NULL
                AND document_verification_at IS NULL
            )
            OR
            (
                document_verification_status IN ('APPROVED','REJECTED')
                AND document_verification_by_user_id IS NOT NULL
                AND document_verification_at IS NOT NULL
            )
        ),

    -- The overall case remains PENDING_REVIEW while any mandatory stage
    -- remains pending. It can be APPROVED only when both stages are
    -- approved, and it can be REJECTED only when at least one stage
    -- has been rejected.
    CONSTRAINT ck_kyc_case_overall_state
        CHECK (
            (
                status = 'PENDING_REVIEW'
                AND identity_verification_status != 'REJECTED'
                AND document_verification_status != 'REJECTED'
                AND decision_by_user_id IS NULL
                AND decision_at IS NULL
            )
            OR
            (
                status = 'APPROVED'
                AND identity_verification_status = 'APPROVED'
                AND document_verification_status = 'APPROVED'
                AND decision_by_user_id IS NOT NULL
                AND decision_at IS NOT NULL
            )
            OR
            (
                status = 'REJECTED'
                AND (
                    identity_verification_status = 'REJECTED'
                    OR document_verification_status = 'REJECTED'
                )
                AND decision_by_user_id IS NOT NULL
                AND decision_at IS NOT NULL
            )
        )
);

CREATE INDEX IF NOT EXISTS ix_kyc_cases_status
    ON kyc_cases (status);

-- Work queues are listed per branch.
CREATE INDEX IF NOT EXISTS ix_kyc_cases_branch_status
    ON kyc_cases (branch_code, status, created_at);

CREATE INDEX IF NOT EXISTS ix_kyc_cases_assigned_officer
    ON kyc_cases (assigned_officer_user_id);

-- Evidence and case history are looked up per customer.
CREATE INDEX IF NOT EXISTS ix_kyc_cases_customer_number
    ON kyc_cases (customer_number);

CREATE TABLE IF NOT EXISTS outbox_messages (
    id UUID PRIMARY KEY,

    -- Monotonic insertion order, assigned by the database. Events raised together
    -- (e.g. a stage decision and the resulting case decision) can share the same
    -- occurred_at; the relay publishes each aggregate's events strictly in this order.
    sequence BIGINT GENERATED ALWAYS AS IDENTITY,

    aggregate_type VARCHAR(100) NOT NULL,
    aggregate_id VARCHAR(100) NOT NULL,
    event_type VARCHAR(200) NOT NULL,
    payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,

    workflow_id UUID NULL,
    correlation_id UUID NULL,
    causation_id UUID NULL,

    -- Human originator of the long-running workflow.
    initiated_by_user_id VARCHAR(200) NULL,

    -- Human who actually performed the KYC decision, when applicable.
    acted_by_user_id VARCHAR(200) NULL,

    published_at TIMESTAMPTZ NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_attempt_at TIMESTAMPTZ NULL,
    last_error TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_kyc_outbox_sequence
    ON outbox_messages (sequence);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_unpublished
    ON outbox_messages (sequence)
    WHERE published_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_aggregate_unpublished
    ON outbox_messages (aggregate_type, aggregate_id, sequence)
    WHERE published_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_workflow_id ON outbox_messages (workflow_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_correlation_id ON outbox_messages (correlation_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_causation_id ON outbox_messages (causation_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_initiated_by_user_id ON outbox_messages (initiated_by_user_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_acted_by_user_id ON outbox_messages (acted_by_user_id);
-- Inbox (idempotent consumer): one row per consumed message and consumer, written
-- in the SAME transaction as the business change the message caused. A redelivered
-- message is recognised and not applied twice. Additive: safe to run on an existing
-- database (CREATE ... IF NOT EXISTS).
CREATE TABLE IF NOT EXISTS inbox_messages (
    id UUID NOT NULL,
    message_id UUID NOT NULL,
    consumer VARCHAR(200) NOT NULL,
    received_at TIMESTAMPTZ NOT NULL,
    processed_at TIMESTAMPTZ NULL,

    CONSTRAINT pk_kyc_inbox_messages PRIMARY KEY (id),
    CONSTRAINT uq_kyc_inbox_messages_message_consumer UNIQUE (message_id, consumer)
);

CREATE INDEX IF NOT EXISTS ix_kyc_inbox_messages_message_id ON inbox_messages (message_id);

-- The KYC API role gets the table through the default privileges of
-- db/EwpServiceDbUsers.sql; granted explicitly too, in case that ran earlier.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ewp_kyc_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON inbox_messages TO ewp_kyc_api;
    END IF;
END $$;
