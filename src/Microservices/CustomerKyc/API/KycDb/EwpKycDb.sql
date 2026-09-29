DROP TABLE IF EXISTS kyc_cases CASCADE;
DROP TABLE IF EXISTS outbox_messages CASCADE;

CREATE TABLE IF NOT EXISTS kyc_cases (
    id BIGSERIAL PRIMARY KEY,
    customer_number VARCHAR(100) NOT NULL,
    status VARCHAR(50) NOT NULL,
    initiated_by_user_id VARCHAR(200) NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_kyc_cases_customer_number UNIQUE (customer_number),
    CONSTRAINT ck_kyc_cases_status CHECK (status IN ('PENDING_REVIEW','APPROVED','REJECTED'))
);

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

CREATE INDEX IF NOT EXISTS ix_kyc_outbox_unpublished ON outbox_messages (occurred_at) WHERE published_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_workflow_id ON outbox_messages (workflow_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_correlation_id ON outbox_messages (correlation_id);
CREATE INDEX IF NOT EXISTS ix_kyc_outbox_causation_id ON outbox_messages (causation_id);