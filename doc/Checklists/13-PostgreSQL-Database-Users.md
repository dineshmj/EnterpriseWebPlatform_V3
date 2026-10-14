# 13 — PostgreSQL database users (per database)

Scripts:
- `db/EwpServiceDbUsers.sql`: logins, `CONNECT`, schema and default privileges.
- `db/EwpBffStateDb.sql`: BFF schemas.
- Each database's own full script.

## 1. One login per deployable

- [x] **Every deployable has its own login.** No component uses `postgres`. *Because* a leaked connection string exposes one component's data, not everybody's.
- [x] **Logins are `NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT`.**
- [x] **The script is idempotent:** re-running it updates passwords and grants.

## 2. One database per context

- [x] **`CONNECT` is revoked from `PUBLIC` on every database** and granted only to its own login(s):

  | Database | Login(s) |
  | --- | --- |
  | `EwpIdentityAccessDb` | `ewp_idp` |
  | `EwpBssShellDb` | `ewp_shell` |
  | `EwpCustomerDb` | `ewp_customer_onboarding_api`, `ewp_customer_outbox_relay` |
  | `EwpKycDb` | `ewp_kyc_api` |
  | `EwpDocumentsManagementDb` | `ewp_documents_api` |
  | `EwpComplianceDb` | `ewp_compliance_api` |
  | `EwpAccountsDb` | `ewp_accounts_api` |
  | `EwpNotificationsDb` | `ewp_notifications_api` |
  | `EwpPaymentsDb` | `ewp_payments_api` |
  | `EwpAuditDb` | `ewp_audit_api` |
  | `EwpBffStateDb` | one login per BFF: `ewp_shell`, `ewp_co_bff`, `ewp_compliance_bff`, `ewp_accounts_bff`, `ewp_payments_bff`, `ewp_kyc_bff`, `ewp_audit_web`, `ewp_operations_web` |

  *Because* database-per-context is enforced by the server, not by convention.
- [x] **No DDL for application logins:** `CREATE` on `public` is revoked from `PUBLIC`, and the objects are owned by `postgres`.
- [x] **`EwpBffStateDb` has no objects in `public`.** Each BFF gets `USAGE` and data access on ITS OWN schema only (`shell_bff`, `co_bff`, …, `kyc_bff`, `audit_bff`, `operations_bff`). *Because* one BFF's credentials cannot read another BFF's sessions or keys.
- [x] **Default privileges** (`ALTER DEFAULT PRIVILEGES FOR ROLE postgres`) give new tables the same grants. *Because* a recreated table never loses its grant, and never gains a wider one.

## 3. Production (open)

- [ ] **Passwords in a secret store,** with rotation; the passwords in the script are Development values (R-10).
- [ ] **TLS to PostgreSQL** (`sslmode=verify-full`), with network rules so that only the component's own pods can reach its database.
- [ ] **Database ownership by a migration role,** separate from `postgres`.