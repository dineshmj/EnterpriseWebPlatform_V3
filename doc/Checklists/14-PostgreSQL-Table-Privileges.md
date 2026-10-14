# 14 — PostgreSQL table privileges (inside each database)

Scripts: `db/EwpServiceDbUsers.sql` and each database's own full script

## 1. What each login may do

| Login | Tables | Privileges |
| --- | --- | --- |
| `ewp_idp` | all in `public` and `identity_server` | `SELECT, INSERT, UPDATE, DELETE`; sequences `USAGE, SELECT, UPDATE` |
| `ewp_shell` | all (menu) | `SELECT, INSERT, UPDATE, DELETE` |
| `ewp_customer_onboarding_api` | all in `EwpCustomerDb` | `SELECT, INSERT, UPDATE, DELETE` |
| `ewp_customer_outbox_relay` | **`outbox_messages` only** | **`SELECT, UPDATE`**: no insert, no delete, no other table |
| `ewp_kyc_api`, `ewp_documents_api`, `ewp_compliance_api`, `ewp_accounts_api`, `ewp_notifications_api`, `ewp_payments_api` | all in their own database | `SELECT, INSERT, UPDATE, DELETE` |
| `ewp_audit_api` | all in `EwpAuditDb` | **`SELECT, INSERT` only**: no update, no delete |
| BFF logins | all in their own schema of `EwpBffStateDb` | `SELECT, INSERT, UPDATE, DELETE` |

## 2. Checks

- [x] **The separate Outbox relay can only read and mark its outbox rows.** *Because* a compromised relay cannot change customers or applications.
- [x] **The audit trail is append-only, twice over.** The login has `SELECT` and `INSERT` only, and triggers refuse `UPDATE`, `DELETE` and `TRUNCATE` for EVERY user, including `postgres`. The hash chain and the external anchors catch whoever bypasses both. → `src/Microservices/Audit/API/AuditDb/EwpAuditDb.sql`
- [x] **No login has `TRUNCATE`, `REFERENCES` or `TRIGGER`;** grants list the exact privileges, never `ALL`.
- [x] **Check constraints in each schema keep invalid states out,** even for a buggy application: status values, amounts and the four-eyes columns.
- [ ] **Hardening idea: narrower grants per table for the domain APIs.** For example:
  - no `DELETE` on `outbox_messages`, `inbox_messages` or history and timeline tables;
  - `UPDATE` only on the columns that change.

  Today each domain API may `SELECT, INSERT, UPDATE, DELETE` every table of its own database.
- [ ] **Production: row-level security** is not needed while each context has its own database and branch scoping happens in the queries. Revisit it only if databases are ever shared.