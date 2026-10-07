-- =============================================================================
-- EwpPaymentsDb - Payments bounded context (orchestrated saga)
-- =============================================================================
-- Create the database once (as postgres):  CREATE DATABASE "EwpPaymentsDb";
-- Then run while connected to EwpPaymentsDb (it drops and recreates the tables):
--   psql -h localhost -U postgres -d EwpPaymentsDb -v ON_ERROR_STOP=1 -f EwpPaymentsDb.sql
-- The CHECK constraints mirror the Payment and PaymentSaga aggregates' invariants.
-- =============================================================================

DROP TABLE IF EXISTS payment_saga_history CASCADE;
DROP TABLE IF EXISTS payment_sagas CASCADE;
DROP TABLE IF EXISTS payments CASCADE;
DROP TABLE IF EXISTS outbox_messages CASCADE;
DROP TABLE IF EXISTS inbox_messages CASCADE;
DROP TABLE IF EXISTS staff_members CASCADE;

-- One payment instruction, captured by a staff member for a customer (assisted channel).
CREATE TABLE payments (
    id BIGSERIAL PRIMARY KEY,

    -- The request's Idempotency-Key: the same key always finds the same payment; also the
    -- idempotency key of every command to Accounts and every call to the payment network.
    payment_ref UUID NOT NULL,
    payment_number VARCHAR(30) NOT NULL,

    -- The paying customer and account (Customer Onboarding / Accounts, by value).
    customer_number VARCHAR(100) NOT NULL,
    from_bsb VARCHAR(7) NOT NULL,
    from_account_number VARCHAR(9) NOT NULL,

    -- The payee, as captured: account name, BSB and account number.
    payee_name VARCHAR(140) NOT NULL,
    to_bsb VARCHAR(7) NOT NULL,
    to_account_number VARCHAR(9) NOT NULL,

    amount NUMERIC(18,2) NOT NULL,
    currency CHAR(3) NOT NULL,
    reference VARCHAR(35) NULL,

    -- ABAC: only the capturing branch sees the payment. SoD (5b): the initiator never approves.
    branch_code VARCHAR(20) NOT NULL,
    initiated_by_user_id VARCHAR(200) NOT NULL,

    -- Decided at initiation from the configured tier (Payments:ApprovalThreshold).
    approval_required BOOLEAN NOT NULL,

    status VARCHAR(30) NOT NULL,
    -- While COMPENSATING: where the payment ends once the funds are released.
    compensation_outcome VARCHAR(30) NULL,
    outcome_code VARCHAR(40) NULL,
    outcome_reason VARCHAR(1000) NULL,
    network_reference VARCHAR(100) NULL,

    -- The payments officer's decision (approval tier): never the initiator (SoD), within the
    -- officer's clearance limit (ABAC). Remarks are required for a rejection.
    decision_by_user_id VARCHAR(200) NULL,
    decision_at TIMESTAMPTZ NULL,
    decision_remarks VARCHAR(1000) NULL,

    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    ended_at TIMESTAMPTZ NULL,
    version BIGINT NOT NULL,

    CONSTRAINT uq_payments_payment_ref UNIQUE (payment_ref),
    CONSTRAINT uq_payments_payment_number UNIQUE (payment_number),

    CONSTRAINT ck_payments_status CHECK (status IN (
        'INITIATED', 'RESERVING_FUNDS', 'PENDING_APPROVAL', 'SENDING_TO_NETWORK', 'SETTLING_FUNDS',
        'COMPLETED', 'REJECTED', 'COMPENSATING', 'FAILED', 'COMPENSATION_FAILED')),
    CONSTRAINT ck_payments_compensation_outcome CHECK (compensation_outcome IS NULL OR compensation_outcome IN ('FAILED', 'REJECTED')),
    CONSTRAINT ck_payments_amount CHECK (amount > 0 AND amount <= 1000000),
    CONSTRAINT ck_payments_bsbs CHECK (from_bsb ~ '^[0-9]{3}-[0-9]{3}$' AND to_bsb ~ '^[0-9]{3}-[0-9]{3}$'),
    CONSTRAINT ck_payments_account_numbers CHECK (from_account_number ~ '^[0-9]{5,9}$' AND to_account_number ~ '^[0-9]{5,9}$'),
    CONSTRAINT ck_payments_different_accounts CHECK (from_bsb <> to_bsb OR from_account_number <> to_account_number),

    -- Ended exactly when COMPLETED, REJECTED or FAILED; a payment that did not complete says why.
    CONSTRAINT ck_payments_ended CHECK ((status IN ('COMPLETED', 'REJECTED', 'FAILED')) = (ended_at IS NOT NULL)),
    CONSTRAINT ck_payments_outcome CHECK (status NOT IN ('REJECTED', 'FAILED', 'COMPENSATING', 'COMPENSATION_FAILED')
                                          OR (outcome_code IS NOT NULL AND outcome_reason IS NOT NULL)),
    -- The network accepted the payment exactly before settling / completing.
    CONSTRAINT ck_payments_network_reference CHECK (status NOT IN ('SETTLING_FUNDS', 'COMPLETED') OR network_reference IS NOT NULL),

    CONSTRAINT ck_payments_decision CHECK ((decision_by_user_id IS NULL) = (decision_at IS NULL)),
    CONSTRAINT ck_payments_decider_not_initiator CHECK (decision_by_user_id IS NULL OR decision_by_user_id <> initiated_by_user_id),
    CONSTRAINT ck_payments_decision_only_when_required CHECK (decision_by_user_id IS NULL OR approval_required),

    CONSTRAINT ck_payments_version CHECK (version > 0)
);

CREATE INDEX ix_payments_branch_created ON payments (branch_code, created_at DESC);
CREATE INDEX ix_payments_branch_status ON payments (branch_code, status);
CREATE INDEX ix_payments_customer_number ON payments (customer_number);

-- The ORCHESTRATOR's persisted state: one saga per payment. It records which step the
-- payment is at, which command it waits on and when to look again; the step runner and
-- the reply handler lock this row, so one payment is never worked on twice at once.
CREATE TABLE payment_sagas (
    id UUID PRIMARY KEY,
    payment_id BIGINT NOT NULL REFERENCES payments (id),
    payment_ref UUID NOT NULL,

    step VARCHAR(30) NOT NULL,
    status VARCHAR(30) NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_check_at TIMESTAMPTZ NULL,
    last_error VARCHAR(1000) NULL,

    -- The command the current step waits on, and the last message that moved the saga
    -- (the CausationId of the next message it sends).
    current_command_id UUID NULL,
    last_message_id UUID NULL,

    workflow_id UUID NOT NULL,
    correlation_id UUID NOT NULL,
    initiated_by_user_id VARCHAR(200) NOT NULL,

    -- W3C traceparent of the request that started the payment: background steps continue it.
    trace_parent VARCHAR(55) NULL,

    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    version BIGINT NOT NULL,

    CONSTRAINT uq_payment_sagas_payment_id UNIQUE (payment_id),
    CONSTRAINT uq_payment_sagas_payment_ref UNIQUE (payment_ref),
    CONSTRAINT ck_payment_sagas_step CHECK (step IN ('RESERVE_FUNDS', 'AWAIT_APPROVAL', 'SEND_TO_NETWORK', 'SETTLE_FUNDS', 'RELEASE_FUNDS', 'DONE')),
    CONSTRAINT ck_payment_sagas_status CHECK (status IN ('RUNNING', 'WAITING_FOR_PERSON', 'FINISHED', 'STUCK')),
    -- A running saga always has a timer; a finished one never; DONE exactly when FINISHED.
    CONSTRAINT ck_payment_sagas_timer CHECK ((status = 'RUNNING') = (next_check_at IS NOT NULL)),
    CONSTRAINT ck_payment_sagas_done CHECK ((step = 'DONE') = (status = 'FINISHED')),
    CONSTRAINT ck_payment_sagas_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_payment_sagas_version CHECK (version > 0)
);

-- The step runner's work list: running sagas by due time.
CREATE INDEX ix_payment_sagas_due ON payment_sagas (next_check_at) WHERE status = 'RUNNING';

-- Everything the saga did and received, in order: the payment's timeline.
CREATE TABLE payment_saga_history (
    id BIGSERIAL PRIMARY KEY,
    saga_id UUID NOT NULL REFERENCES payment_sagas (id),
    at TIMESTAMPTZ NOT NULL,
    step VARCHAR(30) NOT NULL,
    kind VARCHAR(40) NOT NULL,
    detail VARCHAR(2000) NOT NULL,
    message_id UUID NULL
);

CREATE INDEX ix_payment_saga_history_saga ON payment_saga_history (saga_id, id);

-- Transactional Outbox (same shape as the other contexts): the saga's commands
-- (accounts.commands) and the payments' outcomes (payments.payment.events).
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

CREATE UNIQUE INDEX ux_payments_outbox_sequence ON outbox_messages (sequence);
CREATE INDEX ix_payments_outbox_unpublished ON outbox_messages (sequence) WHERE published_at IS NULL;
CREATE INDEX ix_payments_outbox_workflow_id ON outbox_messages (workflow_id);
CREATE INDEX ix_payments_outbox_causation_id ON outbox_messages (causation_id);

-- Inbox (idempotent consumer of Accounts' replies).
CREATE TABLE inbox_messages (
    id UUID NOT NULL,
    message_id UUID NOT NULL,
    consumer VARCHAR(200) NOT NULL,
    received_at TIMESTAMPTZ NOT NULL,
    processed_at TIMESTAMPTZ NULL,
    CONSTRAINT pk_payments_inbox_messages PRIMARY KEY (id),
    CONSTRAINT uq_payments_inbox_messages_message_consumer UNIQUE (message_id, consumer)
);

-- Staff directory: the LAN ID of each staff member this context has seen (from their
-- token). Screens and events show the LAN ID; records and every rule keep the subject ID.
CREATE TABLE staff_members (
    user_id VARCHAR(200) PRIMARY KEY,
    lan_id VARCHAR(20) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

-- The service's own least-privilege user (created by db/EwpServiceDbUsers.sql).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ewp_payments_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON payments, payment_sagas, payment_saga_history, outbox_messages, inbox_messages, staff_members TO ewp_payments_api;
        GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_payments_api;
    ELSE
        RAISE WARNING 'Role ewp_payments_api does not exist yet: run db\Apply-EwpServiceDbUsers.ps1, then this script again.';
    END IF;
END $$;