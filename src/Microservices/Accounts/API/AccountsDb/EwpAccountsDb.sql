-- =============================================================================
-- EwpAccountsDb - Accounts bounded context
-- =============================================================================
-- Run while connected to EwpAccountsDb (it drops and recreates the tables):
--   psql -h localhost -U postgres -d EwpAccountsDb -v ON_ERROR_STOP=1 -f EwpAccountsDb.sql
-- The CHECK constraints mirror the AccountApplication and Account aggregates' invariants.
-- =============================================================================

DROP TABLE IF EXISTS accounts CASCADE;
DROP TABLE IF EXISTS account_applications CASCADE;
DROP TABLE IF EXISTS outbox_messages CASCADE;
DROP TABLE IF EXISTS inbox_messages CASCADE;

CREATE TABLE account_applications (
    id BIGSERIAL PRIMARY KEY,

    -- The onboarding application (owned by Customer Onboarding, referenced by value:
    -- never-repeating GUID + business number). One account application per onboarding
    -- application: also the business idempotency key for its creation.
    application_ref UUID NOT NULL,
    application_number VARCHAR(30) NOT NULL,
    customer_number VARCHAR(100) NOT NULL,

    -- The account holder's name as Compliance cleared it (snapshot from
    -- compliance.case.approved): the name core banking opens the account in.
    holder_first_name VARCHAR(100) NOT NULL,
    holder_last_name VARCHAR(100) NOT NULL,

    -- The Compliance case that approved the application (Compliance, by value).
    compliance_case_id BIGINT NOT NULL,

    -- ABAC: officers see and decide only their own branch's applications.
    branch_code VARCHAR(20) NOT NULL,

    -- Separation of Duties: neither of these people may take or decide this application.
    initiated_by_user_id VARCHAR(200) NULL,
    compliance_approved_by_user_id VARCHAR(200) NULL,

    status VARCHAR(30) NOT NULL,
    product VARCHAR(30) NULL,

    -- ReBAC: the officer the application is assigned to (NULL = shared work queue).
    assigned_officer_user_id VARCHAR(200) NULL,

    hold_reason VARCHAR(4000) NULL,

    decision_by_user_id VARCHAR(200) NULL,
    decision_at TIMESTAMPTZ NULL,
    decision_remarks VARCHAR(4000) NULL,
    -- The officer's decision command: the CausationId of what it led to (incl. the later opening).
    decision_id UUID NULL,

    -- Opening by the external core-banking system. A technical failure keeps the
    -- application OPENING and is retried; too many failures or a refusal end it FAILED.
    opening_attempts INTEGER NOT NULL DEFAULT 0,
    next_opening_at TIMESTAMPTZ NULL,
    last_opening_error TEXT NULL,
    account_number VARCHAR(30) NULL,
    opened_at TIMESTAMPTZ NULL,
    failure_reason TEXT NULL,

    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    version BIGINT NOT NULL,

    CONSTRAINT uq_account_applications_application_ref UNIQUE (application_ref),

    CONSTRAINT ck_account_applications_status
        CHECK (status IN ('PENDING_REVIEW', 'ON_HOLD', 'REJECTED', 'OPENING', 'OPENED', 'FAILED')),

    CONSTRAINT ck_account_applications_product
        CHECK (product IS NULL OR product IN ('EVERYDAY_TRANSACTION', 'SAVINGS')),

    -- An officer decision exists exactly when the application is decided.
    CONSTRAINT ck_account_applications_decision
        CHECK ((status IN ('REJECTED', 'OPENING', 'OPENED', 'FAILED')) = (decision_by_user_id IS NOT NULL AND decision_at IS NOT NULL)),

    -- An approved application always names its product.
    CONSTRAINT ck_account_applications_approved_product
        CHECK (status NOT IN ('OPENING', 'OPENED', 'FAILED') OR product IS NOT NULL),

    -- A rejection always carries remarks.
    CONSTRAINT ck_account_applications_rejection_remarks
        CHECK (status <> 'REJECTED' OR decision_remarks IS NOT NULL),

    CONSTRAINT ck_account_applications_hold_reason
        CHECK (status <> 'ON_HOLD' OR hold_reason IS NOT NULL),

    -- Opened exactly when there is an account; failed exactly when there is a reason.
    CONSTRAINT ck_account_applications_opened
        CHECK ((status = 'OPENED') = (account_number IS NOT NULL AND opened_at IS NOT NULL)),

    CONSTRAINT ck_account_applications_failed
        CHECK ((status = 'FAILED') = (failure_reason IS NOT NULL)),

    CONSTRAINT ck_account_applications_version CHECK (version > 0)
);

CREATE INDEX ix_account_applications_branch_status ON account_applications (branch_code, status);
CREATE INDEX ix_account_applications_opening_due ON account_applications (next_opening_at) WHERE status = 'OPENING';
CREATE INDEX ix_account_applications_customer_number ON account_applications (customer_number);

-- Accounts opened by the core-banking system (it issues the BSB and account number).
CREATE TABLE accounts (
    id BIGSERIAL PRIMARY KEY,
    account_number VARCHAR(30) NOT NULL,
    bsb VARCHAR(7) NOT NULL,

    -- The account holder (Customer Onboarding's customer, by value): ReBAC "owns".
    customer_number VARCHAR(100) NOT NULL,
    holder_first_name VARCHAR(100) NOT NULL,
    holder_last_name VARCHAR(100) NOT NULL,
    application_ref UUID NOT NULL,
    branch_code VARCHAR(20) NOT NULL,
    product VARCHAR(30) NOT NULL,
    status VARCHAR(20) NOT NULL,
    core_banking_reference VARCHAR(100) NOT NULL,
    opened_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    version BIGINT NOT NULL,

    CONSTRAINT uq_accounts_bsb_account_number UNIQUE (bsb, account_number),
    CONSTRAINT uq_accounts_application_ref UNIQUE (application_ref),

    CONSTRAINT ck_accounts_bsb CHECK (bsb ~ '^[0-9]{3}-[0-9]{3}$'),
    CONSTRAINT ck_accounts_product CHECK (product IN ('EVERYDAY_TRANSACTION', 'SAVINGS')),
    CONSTRAINT ck_accounts_status CHECK (status IN ('ACTIVE', 'FROZEN', 'CLOSED')),
    CONSTRAINT ck_accounts_version CHECK (version > 0)
);

CREATE INDEX ix_accounts_branch_opened ON accounts (branch_code, opened_at);
CREATE INDEX ix_accounts_customer_number ON accounts (customer_number);

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

CREATE UNIQUE INDEX ux_accounts_outbox_sequence ON outbox_messages (sequence);
CREATE INDEX ix_accounts_outbox_unpublished ON outbox_messages (sequence) WHERE published_at IS NULL;
CREATE INDEX ix_accounts_outbox_workflow_id ON outbox_messages (workflow_id);
CREATE INDEX ix_accounts_outbox_causation_id ON outbox_messages (causation_id);

-- Inbox (idempotent consumer).
CREATE TABLE inbox_messages (
    id UUID NOT NULL,
    message_id UUID NOT NULL,
    consumer VARCHAR(200) NOT NULL,
    received_at TIMESTAMPTZ NOT NULL,
    processed_at TIMESTAMPTZ NULL,
    CONSTRAINT pk_accounts_inbox_messages PRIMARY KEY (id),
    CONSTRAINT uq_accounts_inbox_messages_message_consumer UNIQUE (message_id, consumer)
);

-- The service's own least-privilege user (created by db/EwpServiceDbUsers.sql).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ewp_accounts_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON account_applications, accounts, outbox_messages, inbox_messages TO ewp_accounts_api;
        GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_accounts_api;
    ELSE
        RAISE WARNING 'Role ewp_accounts_api does not exist yet: run db\Apply-EwpServiceDbUsers.ps1, then this script again.';
    END IF;
END $$;