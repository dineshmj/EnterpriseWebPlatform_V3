-- =============================================================================
-- EwpAccountsDb - upgrade for Payments (step 5a): balances and funds holds
-- =============================================================================
-- For a database created BEFORE step 5a, when its accounts must be kept (they match
-- COMPLETED onboardings in the other contexts). Safe to re-run. A fresh database gets
-- the same objects from EwpAccountsDb.sql instead.
--   psql -h localhost -U postgres -d EwpAccountsDb -v ON_ERROR_STOP=1 -f Upgrade-5a-Funds.sql
-- =============================================================================

ALTER TABLE accounts ADD COLUMN IF NOT EXISTS currency CHAR(3) NOT NULL DEFAULT 'AUD';
ALTER TABLE accounts ADD COLUMN IF NOT EXISTS balance NUMERIC(18,2) NOT NULL DEFAULT 0;
ALTER TABLE accounts ADD COLUMN IF NOT EXISTS held_amount NUMERIC(18,2) NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_accounts_funds') THEN
        ALTER TABLE accounts ADD CONSTRAINT ck_accounts_funds CHECK (held_amount >= 0 AND balance >= held_amount);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS funds_holds (
    id BIGSERIAL PRIMARY KEY,
    payment_ref UUID NOT NULL,
    payment_number VARCHAR(30) NOT NULL,
    account_id BIGINT NULL REFERENCES accounts (id),
    bsb VARCHAR(7) NOT NULL,
    account_number VARCHAR(30) NOT NULL,
    customer_number VARCHAR(100) NOT NULL,
    amount NUMERIC(18,2) NOT NULL,
    currency CHAR(3) NOT NULL,
    status VARCHAR(20) NOT NULL,
    refusal_reason VARCHAR(40) NULL,
    initiated_by_user_id VARCHAR(200) NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    settled_at TIMESTAMPTZ NULL,
    released_at TIMESTAMPTZ NULL,
    version BIGINT NOT NULL,
    CONSTRAINT uq_funds_holds_payment_ref UNIQUE (payment_ref),
    CONSTRAINT ck_funds_holds_status CHECK (status IN ('HELD', 'REFUSED', 'SETTLED', 'RELEASED')),
    CONSTRAINT ck_funds_holds_amount CHECK (amount >= 0),
    CONSTRAINT ck_funds_holds_refusal CHECK ((status = 'REFUSED') = (refusal_reason IS NOT NULL)),
    CONSTRAINT ck_funds_holds_account CHECK (status NOT IN ('HELD', 'SETTLED') OR (account_id IS NOT NULL AND amount > 0)),
    CONSTRAINT ck_funds_holds_settled CHECK ((status = 'SETTLED') = (settled_at IS NOT NULL)),
    CONSTRAINT ck_funds_holds_version CHECK (version > 0)
);

CREATE INDEX IF NOT EXISTS ix_funds_holds_account_status ON funds_holds (account_id, status);

-- DEMO ONLY: accounts opened before this upgrade start with the same demo deposit a new
-- account gets (Accounts:DemoOpeningDeposit), so their customers can make payments.
UPDATE accounts SET balance = 5000.00, version = version + 1
WHERE balance = 0 AND held_amount = 0;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ewp_accounts_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON funds_holds TO ewp_accounts_api;
        GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_accounts_api;
    END IF;
END $$;

SELECT bsb, account_number, customer_number, currency, balance, held_amount FROM accounts ORDER BY id;