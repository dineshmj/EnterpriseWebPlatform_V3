-- =============================================================================
-- EwpAuditDb - the platform's tamper-evident audit trail (Audit API)
-- =============================================================================
-- Create the database once (as postgres):  CREATE DATABASE "EwpAuditDb";
-- Then, connected to EwpAuditDb (it DROPS the trail - development only):
--   psql -h localhost -U postgres -d EwpAuditDb -v ON_ERROR_STOP=1 -f EwpAuditDb.sql
-- and run ps\database\Apply-EwpServiceDbUsers.ps1 (user ewp_audit_api: SELECT and INSERT only).
--
-- Three layers keep the trail honest:
--   1. the Audit API's database user may only INSERT and SELECT (no UPDATE, DELETE, DDL);
--   2. a trigger refuses UPDATE, DELETE and TRUNCATE - for every user, the owner included;
--   3. each entry's entry_hash = SHA-256 of its content AND the previous entry's hash, so a
--      change made by someone able to bypass 1 and 2 still breaks the chain from that entry
--      on; the Audit API re-verifies the chain on a schedule and reports where it broke.
-- What is stored: identifiers, people (subject ID + LAN ID), outcome and the SHA-256 of the
-- original message - no names, addresses, contact details or payload copies.
-- =============================================================================

DROP TABLE IF EXISTS audit_entries CASCADE;
DROP FUNCTION IF EXISTS audit_entries_append_only() CASCADE;

CREATE TABLE audit_entries (
    -- Position in the chain: 1, 2, 3, ... without gaps (assigned under an advisory lock).
    sequence             BIGINT        NOT NULL,
    -- The event's MessageId (or, for an ACCESS entry, a new ID): one entry per message.
    message_id           UUID          NOT NULL,
    entry_kind           VARCHAR(10)   NOT NULL,   -- EVENT (a business fact) / ACCESS (someone read the trail)
    event_type           VARCHAR(100)  NOT NULL,
    source               VARCHAR(100)  NULL,       -- producing context, e.g. payments
    topic                VARCHAR(200)  NULL,
    kafka_partition      INTEGER       NULL,
    kafka_offset         BIGINT        NULL,
    occurred_at          TIMESTAMPTZ   NOT NULL,   -- business time of the fact
    recorded_at          TIMESTAMPTZ   NOT NULL,   -- when the trail recorded it
    workflow_id          UUID          NULL,
    correlation_id       UUID          NULL,
    causation_id         UUID          NULL,
    initiated_by_user_id VARCHAR(200)  NULL,       -- the accountable human who started the workflow
    initiated_by_lan_id  VARCHAR(50)   NULL,
    actor_user_id        VARCHAR(200)  NULL,       -- who made THIS decision (officer, approver)
    actor_lan_id         VARCHAR(50)   NULL,
    record_type          VARCHAR(20)   NULL,       -- PAYMENT / APPLICATION / CUSTOMER
    record_ref           VARCHAR(100)  NULL,       -- e.g. PAY-..., APP-..., CUST-...
    customer_number      VARCHAR(50)   NULL,
    branch_code          VARCHAR(20)   NULL,
    status               VARCHAR(100)  NULL,
    reason_code          VARCHAR(100)  NULL,
    amount               NUMERIC(18,2) NULL,
    currency             CHAR(3)       NULL,
    payload_sha256       CHAR(64)      NULL,       -- evidence of exactly what was received
    previous_hash        CHAR(64)      NOT NULL,
    entry_hash           CHAR(64)      NOT NULL,

    CONSTRAINT pk_audit_entries PRIMARY KEY (sequence),
    CONSTRAINT uq_audit_entries_message UNIQUE (message_id),
    CONSTRAINT uq_audit_entries_hash UNIQUE (entry_hash),
    CONSTRAINT ck_audit_entries_sequence CHECK (sequence > 0),
    CONSTRAINT ck_audit_entries_kind CHECK (entry_kind IN ('EVENT', 'ACCESS'))
);

-- What an auditor searches by.
CREATE INDEX ix_audit_entries_record ON audit_entries (record_ref);
CREATE INDEX ix_audit_entries_customer ON audit_entries (customer_number);
CREATE INDEX ix_audit_entries_workflow ON audit_entries (workflow_id);
CREATE INDEX ix_audit_entries_actor ON audit_entries (actor_user_id);
CREATE INDEX ix_audit_entries_initiator ON audit_entries (initiated_by_user_id);
CREATE INDEX ix_audit_entries_occurred ON audit_entries (occurred_at);
CREATE INDEX ix_audit_entries_event_type ON audit_entries (event_type);

-- Append-only, for everybody: an entry, once written, is never changed or removed.
CREATE FUNCTION audit_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_entries is append-only: % is not allowed', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER trg_audit_entries_no_change
    BEFORE UPDATE OR DELETE ON audit_entries
    FOR EACH ROW EXECUTE FUNCTION audit_entries_append_only();

CREATE TRIGGER trg_audit_entries_no_truncate
    BEFORE TRUNCATE ON audit_entries
    FOR EACH STATEMENT EXECUTE FUNCTION audit_entries_append_only();

SELECT 'audit_entries ready (append-only, hash-chained)' AS result;