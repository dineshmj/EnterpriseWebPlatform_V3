DROP TABLE IF EXISTS kyc_cases CASCADE;
DROP TABLE IF EXISTS outbox_messages CASCADE;

CREATE TABLE IF NOT EXISTS kyc_cases (
    id BIGSERIAL PRIMARY KEY,
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

    CONSTRAINT uq_kyc_cases_customer_number UNIQUE (customer_number),

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

CREATE TABLE IF NOT EXISTS outbox_messages (
    id UUID PRIMARY KEY,
    aggregate_type VARCHAR(100) NOT NULL,
    aggregate_id VARCHAR(100) NOT NULL,
    event_type VARCHAR(200) NOT NULL,
    payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,

    workflow_id UUID NULL,
    correlation_id UUID NULL,
    causation_id UUID NULL,

    published_at TIMESTAMPTZ NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_attempt_at TIMESTAMPTZ NULL,
    last_error TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_kyc_outbox_unpublished
    ON outbox_messages (occurred_at)
    WHERE published_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_workflow_id ON outbox_messages (workflow_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_correlation_id ON outbox_messages (correlation_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_causation_id ON outbox_messages (causation_id);
