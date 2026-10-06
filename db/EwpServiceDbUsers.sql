-- =============================================================================
-- EWP V3 - least-privilege database users (OWASP A01 / A05)
-- =============================================================================
-- One login role per deployable, with data access (SELECT/INSERT/UPDATE/DELETE)
-- to ITS OWN database only: no DDL, no superuser, no access to other services'
-- databases. A compromised service can no longer read or change every database.
--
-- Run as postgres with psql (it uses \connect - pgAdmin's Query Tool cannot run it),
-- after the databases exist. Easiest, from the repository root:
--   .\db\Apply-EwpServiceDbUsers.ps1
-- or directly:
--   psql -h localhost -U postgres -d postgres -f db\EwpServiceDbUsers.sql
-- It is idempotent: safe to re-run. ALTER DEFAULT PRIVILEGES makes the grants
-- survive recreating a database's tables with its consolidated script (run as
-- postgres). The outbox relay's narrower grant lives in EwpCustomerDb.sql.
--
-- DEVELOPMENT PASSWORDS ONLY (they match the appsettings files). Outside
-- Development, create the roles with secrets from a secret store.
-- =============================================================================

\set ON_ERROR_STOP on

-- -----------------------------------------------------------------------------
-- 1. Login roles (cluster-wide)
-- -----------------------------------------------------------------------------
DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN SELECT * FROM (VALUES
        ('ewp_idp',                     'ewp-idp-dev'),
        ('ewp_shell',                   'ewp-shell-dev'),
        ('ewp_customer_onboarding_api', 'ewp-co-api-dev'),
        ('ewp_customer_outbox_relay',   'ewp-co-relay-dev'),
        ('ewp_kyc_api',                 'ewp-kyc-api-dev'),
        ('ewp_documents_api',           'ewp-dm-api-dev'),
        ('ewp_compliance_api',          'ewp-compliance-api-dev'),
        ('ewp_accounts_api',            'ewp-accounts-api-dev'),
        ('ewp_notifications_api',       'ewp-notifications-api-dev'),
        ('ewp_payments_api',            'ewp-payments-api-dev')
    ) AS t(role_name, role_password)
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r.role_name) THEN
            EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT', r.role_name, r.role_password);
        ELSE
            EXECUTE format('ALTER ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT', r.role_name, r.role_password);
        END IF;
    END LOOP;
END
$$;

-- -----------------------------------------------------------------------------
-- 2. Database isolation: only the owning service (and postgres) may connect
-- -----------------------------------------------------------------------------
REVOKE CONNECT ON DATABASE "EwpIdentityAccessDb"      FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpBssShellDb"            FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpCustomerDb"            FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpKycDb"                 FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpDocumentsManagementDb" FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpComplianceDb"          FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpAccountsDb"            FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpNotificationsDb"       FROM PUBLIC;
REVOKE CONNECT ON DATABASE "EwpPaymentsDb"            FROM PUBLIC;

GRANT CONNECT ON DATABASE "EwpIdentityAccessDb"      TO ewp_idp;
GRANT CONNECT ON DATABASE "EwpBssShellDb"            TO ewp_shell;
GRANT CONNECT ON DATABASE "EwpCustomerDb"            TO ewp_customer_onboarding_api, ewp_customer_outbox_relay;
GRANT CONNECT ON DATABASE "EwpKycDb"                 TO ewp_kyc_api;
GRANT CONNECT ON DATABASE "EwpDocumentsManagementDb" TO ewp_documents_api;
GRANT CONNECT ON DATABASE "EwpComplianceDb"          TO ewp_compliance_api;
GRANT CONNECT ON DATABASE "EwpAccountsDb"            TO ewp_accounts_api;
GRANT CONNECT ON DATABASE "EwpNotificationsDb"       TO ewp_notifications_api;
GRANT CONNECT ON DATABASE "EwpPaymentsDb"            TO ewp_payments_api;

-- -----------------------------------------------------------------------------
-- 3. Per database: data access for the owning service role
--    (existing objects now + objects created later by postgres)
-- -----------------------------------------------------------------------------
\connect EwpIdentityAccessDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_idp;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_idp;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_idp;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_idp;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_idp;

\connect EwpBssShellDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_shell;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_shell;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_shell;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_shell;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_shell;

\connect EwpCustomerDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_customer_onboarding_api, ewp_customer_outbox_relay;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_customer_onboarding_api;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_customer_onboarding_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_customer_onboarding_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_customer_onboarding_api;
-- The outbox relay may only read and mark Outbox rows (granted per table, so it
-- is (re)applied at the end of EwpCustomerDb.sql as well).
GRANT SELECT, UPDATE ON outbox_messages TO ewp_customer_outbox_relay;

\connect EwpKycDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_kyc_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_kyc_api;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_kyc_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_kyc_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_kyc_api;

\connect EwpDocumentsManagementDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_documents_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_documents_api;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_documents_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_documents_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_documents_api;

\connect EwpComplianceDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_compliance_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_compliance_api;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_compliance_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_compliance_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_compliance_api;

\connect EwpAccountsDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_accounts_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_accounts_api;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_accounts_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_accounts_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_accounts_api;

\connect EwpNotificationsDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_notifications_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_notifications_api;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_notifications_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_notifications_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_notifications_api;

\connect EwpPaymentsDb
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ewp_payments_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ewp_payments_api;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_payments_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ewp_payments_api;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO ewp_payments_api;