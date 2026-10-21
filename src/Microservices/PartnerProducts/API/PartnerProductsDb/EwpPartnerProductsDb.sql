-- =============================================================================
-- EwpPartnerProductsDb - EWP's side of the Partner Bank's Digital Gold (Partner Products API)
-- =============================================================================
-- Create the database once (as postgres):  CREATE DATABASE "EwpPartnerProductsDb";
-- Then, connected to EwpPartnerProductsDb (it DROPS the tables - development only):
--   psql -h localhost -U postgres -d EwpPartnerProductsDb -v ON_ERROR_STOP=1 -f EwpPartnerProductsDb.sql
-- and run ps\database\Apply-EwpServiceDbUsers.ps1 (user ewp_partner_products_api: SELECT, INSERT, UPDATE).
--
-- EWP acts as the Partner Bank's DISTRIBUTOR at its assisted-channel counter (Partner Bank repo,
-- doc/Architecture.md D14): the customer signs Form VII once (gold_account_applications) and a
-- transfer authorization slip for each purchase (gold_purchases). The Partner Bank is the system
-- of record of the account and its gold; EWP keeps the evidence of what the customer signed (the
-- scan in Documents Management and its SHA-256) and what it sent and was answered.
-- No full card or ID numbers; the customer's mobile is kept because the Partner Bank needs it on
-- the instruction and every slip is checked against it.
-- =============================================================================

DROP TABLE IF EXISTS outbox_messages CASCADE;
DROP TABLE IF EXISTS gold_purchases CASCADE;
DROP TABLE IF EXISTS gold_account_applications CASCADE;

CREATE TABLE gold_account_applications (
    id                      BIGINT        GENERATED ALWAYS AS IDENTITY,
    application_number      VARCHAR(30)   NOT NULL,   -- DGA-yyMMdd-XXXXXX
    customer_number         VARCHAR(20)   NOT NULL,   -- EWP's customer (CUST-...)
    customer_name           VARCHAR(140)  NOT NULL,   -- from EWP's customer record, never typed
    date_of_birth           DATE          NOT NULL,   -- from Form VII, checked against photo ID
    mobile                  VARCHAR(12)   NOT NULL,   -- +614........ (EWP's record, normalised)
    branch_code             VARCHAR(20)   NOT NULL,   -- the recording staff member's branch
    form_number             VARCHAR(32)   NOT NULL,
    form_signed_on          DATE          NOT NULL,
    form_document_id        UUID          NOT NULL,   -- the scan in Documents Management
    form_scan_sha256        CHAR(64)      NOT NULL,
    recorded_by_subject     VARCHAR(200)  NOT NULL,
    recorded_by_lan_id      VARCHAR(50)   NOT NULL,
    recorded_at             TIMESTAMPTZ   NOT NULL,
    status                  VARCHAR(20)   NOT NULL,   -- RECORDED / SENT / OPENED / REFUSED
    partner_account_number  VARCHAR(20)   NULL,       -- the Partner Bank's PB-DGA-..., once open
    status_reason           VARCHAR(500)  NULL,       -- the Partner Bank's reason, when it refused
    instruction_id          UUID          NULL,       -- the signed instruction's jti: fixed at the first send
    sent_at                 TIMESTAMPTZ   NULL,
    sent_by_lan_id          VARCHAR(50)   NULL,
    updated_at              TIMESTAMPTZ   NOT NULL,
    version                 BIGINT        NOT NULL DEFAULT 0,

    CONSTRAINT pk_gold_account_applications PRIMARY KEY (id),
    CONSTRAINT uq_gold_account_applications_instruction UNIQUE (instruction_id),
    CONSTRAINT ck_gold_account_applications_sent CHECK (status = 'RECORDED' OR instruction_id IS NOT NULL),
    CONSTRAINT uq_gold_account_applications_number UNIQUE (application_number),
    CONSTRAINT uq_gold_account_applications_form UNIQUE (form_number),
    CONSTRAINT ck_gold_account_applications_status CHECK (status IN ('RECORDED', 'SENT', 'OPENED', 'REFUSED')),
    CONSTRAINT ck_gold_account_applications_sha CHECK (form_scan_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_gold_account_applications_open CHECK (status <> 'OPENED' OR partner_account_number IS NOT NULL)
);

-- ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
-- │ Once only, enforced by the database whatever the code does: one active application
-- │ per customer (a refused one may be followed by a new Form VII), one per form, one per
-- │ signed instruction; one purchase per slip. A double click, two counters at once or a
-- │ resend after a lost answer can never open two accounts or buy twice on one slip.
-- └──────────────────────────────────────────────────────────────────────────────────────
CREATE UNIQUE INDEX uq_gold_account_applications_active_customer
    ON gold_account_applications (customer_number) WHERE status <> 'REFUSED';
CREATE INDEX ix_gold_account_applications_branch ON gold_account_applications (branch_code, status, id DESC);

CREATE TABLE gold_purchases (
    id                      BIGINT        GENERATED ALWAYS AS IDENTITY,
    purchase_number         VARCHAR(30)   NOT NULL,   -- DGP-yyMMdd-XXXXXX
    application_id          BIGINT        NOT NULL,
    customer_number         VARCHAR(20)   NOT NULL,
    partner_account_number  VARCHAR(20)   NOT NULL,
    amount_aud              NUMERIC(18,2) NOT NULL,
    debtor_bsb              CHAR(7)       NOT NULL,
    debtor_account_number   VARCHAR(9)    NOT NULL,
    branch_code             VARCHAR(20)   NOT NULL,
    slip_number             VARCHAR(32)   NOT NULL,
    slip_signed_on          DATE          NOT NULL,
    slip_mobile             VARCHAR(12)   NOT NULL,
    slip_document_id        UUID          NOT NULL,
    slip_scan_sha256        CHAR(64)      NOT NULL,
    recorded_by_subject     VARCHAR(200)  NOT NULL,
    recorded_by_lan_id      VARCHAR(50)   NOT NULL,
    recorded_at             TIMESTAMPTZ   NOT NULL,
    status                  VARCHAR(20)   NOT NULL,   -- RECORDED / SENT / ORDERED / PAID / ALLOCATED / REFUSED / CANCELLED
    instruction_id          UUID          NULL,       -- the signed purchase instruction's jti: fixed at the first send
    sent_at                 TIMESTAMPTZ   NULL,
    sent_by_lan_id          VARCHAR(50)   NULL,
    partner_order_number    VARCHAR(30)   NULL,       -- the Partner Bank's PB-G-..., once it placed the order
    price_per_gram          NUMERIC(18,2) NULL,       -- the Partner Bank's quote (and its re-quote, if paid after the lock)
    grams                   NUMERIC(18,4) NULL,
    quote_expires_at        TIMESTAMPTZ   NULL,
    payment_ref             UUID          NOT NULL,   -- the payment's Idempotency-Key at EWP Payments: fixed when the slip is recorded
    payment_number          VARCHAR(40)   NULL,       -- PAY-..., once EWP Payments started it
    payment_started_by_lan_id VARCHAR(50) NULL,
    payment_started_at      TIMESTAMPTZ   NULL,
    network_reference       VARCHAR(64)   NULL,       -- the payment network's reference: the payment advice carries it
    paid_at                 TIMESTAMPTZ   NULL,
    allocated_at            TIMESTAMPTZ   NULL,
    status_reason           VARCHAR(500)  NULL,
    updated_at              TIMESTAMPTZ   NOT NULL,
    version                 BIGINT        NOT NULL DEFAULT 0,

    CONSTRAINT pk_gold_purchases PRIMARY KEY (id),
    CONSTRAINT uq_gold_purchases_number UNIQUE (purchase_number),
    -- One transfer authorization slip, one purchase; one signed instruction, one payment.
    CONSTRAINT uq_gold_purchases_slip UNIQUE (slip_number),
    CONSTRAINT uq_gold_purchases_instruction UNIQUE (instruction_id),
    CONSTRAINT uq_gold_purchases_payment UNIQUE (payment_ref),
    CONSTRAINT ck_gold_purchases_sent CHECK (status = 'RECORDED' OR instruction_id IS NOT NULL),
    CONSTRAINT ck_gold_purchases_ordered CHECK (status NOT IN ('ORDERED', 'PAID', 'ALLOCATED', 'CANCELLED') OR partner_order_number IS NOT NULL),
    CONSTRAINT ck_gold_purchases_paid CHECK (status NOT IN ('PAID', 'ALLOCATED') OR network_reference IS NOT NULL),
    CONSTRAINT fk_gold_purchases_application FOREIGN KEY (application_id) REFERENCES gold_account_applications (id),
    CONSTRAINT ck_gold_purchases_amount CHECK (amount_aud > 0 AND amount_aud <= 50000),
    CONSTRAINT ck_gold_purchases_status CHECK (status IN ('RECORDED', 'SENT', 'ORDERED', 'PAID', 'ALLOCATED', 'REFUSED', 'CANCELLED')),
    CONSTRAINT ck_gold_purchases_sha CHECK (slip_scan_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX ix_gold_purchases_customer ON gold_purchases (customer_number, id DESC);

-- ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
-- │ Transactional Outbox: each change's events, written in the change's own transaction
-- │ and relayed to Kafka (partner-products.digital-gold.events) by the API's in-process
-- │ relay - the same table as Accounts and Payments. Kept after publishing (no DELETE for
-- │ the API's user): what EWP told the platform stays next to what it recorded.
-- └──────────────────────────────────────────────────────────────────────────────────────
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
    last_error TEXT NULL,
    -- Operations resend of a PARKED message (same message, fresh attempts): who, when and why.
    redrive_count INTEGER NOT NULL DEFAULT 0 CHECK (redrive_count >= 0),
    last_redriven_at TIMESTAMPTZ NULL,
    last_redriven_by VARCHAR(200) NULL,
    last_redrive_reason VARCHAR(500) NULL
);

CREATE UNIQUE INDEX ux_partner_products_outbox_sequence ON outbox_messages (sequence);
CREATE INDEX ix_partner_products_outbox_unpublished ON outbox_messages (sequence) WHERE published_at IS NULL;
CREATE INDEX ix_partner_products_outbox_aggregate ON outbox_messages (aggregate_type, aggregate_id, sequence);

-- -----------------------------------------------------------------------------
-- Verification queries
-- -----------------------------------------------------------------------------
-- SELECT application_number, customer_number, form_number, status, partner_account_number, recorded_by_lan_id FROM gold_account_applications ORDER BY id DESC;
-- SELECT purchase_number, customer_number, amount_aud, slip_number, status, partner_order_number, payment_number, network_reference, grams FROM gold_purchases ORDER BY id DESC;
-- SELECT sequence, aggregate_id, event_type, published_at, attempt_count, last_error FROM outbox_messages ORDER BY sequence DESC;