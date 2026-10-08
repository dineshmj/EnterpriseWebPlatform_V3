# Audit API

The **Audit** bounded context (generic subdomain): one tamper-evident record of who did what across the platform - every submission, decision, approval, rejection and payment outcome - for auditors. It is the first context built in the customer's pattern: Next.js SPA with a light BFF → NestJS Journey API → this ASP.NET Core **Domain API** (the Journey API and the front end follow in steps 6e-2 and 6e-3).

URL: `https://audit-api.dev.localhost:46378`. Database: `EwpAuditDb` (user `ewp_audit_api`: SELECT and INSERT only). Kafka user: `ewp-audit-api` (read-only).

## How entries get in

```text
every business event topic ──Kafka (read-only user)──► trail subscriber INSIDE this API ──► audit_entries (append)
```

- The Kafka consumer runs inside the API (the shared consume loop: in-place retry while the database is down, a dead-letter topic only for messages that are not JSON at all). **There is no HTTP endpoint that writes an entry**, so nobody can post a fake one.
- Topics: `customer.created`, `onboarding.application.*`, `kyc.case.*`, the four `kyc.*.verification.*` stage decisions, `compliance.case.*`, `accounts.application.*`, `accounts.account.*`, `accounts.funds.replies` and `payments.payment.events`. Commands (`accounts.commands`) and dead-letter topics are not facts and are not read.
- The consumer group (`audit.trail-subscriber`) starts at the **earliest** offset: a new trail first records the history Kafka still holds.
- A redelivered message is recognised by its `MessageId` and recorded once.

## What an entry holds

Identifiers and people, not personal data: event type, source context, when it happened and when it was recorded, workflow / correlation / causation IDs, the accountable initiator and the person who made this decision (subject ID + LAN ID), the record (`PAY-…`, the application number, or the customer number), branch, status, reason code, amount and currency - and the **SHA-256 of the original message** as evidence of exactly what was received. Names, addresses, contact details and payload copies are never stored.

## Tamper evidence

| Layer | What it stops |
|---|---|
| The API's database user may only `INSERT` and `SELECT` | A compromised Audit API rewriting history |
| Trigger: `UPDATE`, `DELETE` and `TRUNCATE` are refused for **everybody**, the owner included | An administrator's "quick fix" |
| **Hash chain**: `entry_hash` = SHA-256 of the entry's content **and** the previous entry's hash; sequence numbers without gaps | Anyone who bypasses both (drops the trigger, edits a row, deletes or inserts one): every hash from that entry on no longer matches |

The API re-verifies the whole chain every `Audit:VerifyIntervalMinutes` (Development: 1, otherwise 10). A broken chain is logged as critical, turns `/health/ready` **Degraded** and shows in the metrics: `ewp_health_value{check="audit-chain",key="brokenAtSequence"}` (0 = intact), so an alert can fire on it.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `/health/live`, `/health/ready` | Liveness (process, consume loop); readiness (database, consumer group, chain) |
| `/metrics` | Prometheus scrape, including `ewp_health_value{check="audit-chain"}` and `ewp_messaging_consumed_total` |
| `GET /v1/audit/entries?record=&person=&eventType=&from=&to=&kind=&pageNumber=&pageSize=` | Search, newest first (`audit.search`). `record` matches the record or customer number; `person` a LAN ID or subject ID (a LAN ID also finds entries that carry only the subject ID); `kind` = `EVENT` (default) or `ACCESS` |
| `GET /v1/audit/records/{recordRef}/timeline` | One record's story, oldest first: its entries and those of the same workflow(s) (`audit.view`) |
| `GET /v1/audit/integrity` | Re-walk the chain now (`audit.view`) |

**Who may read:** only a **delegated** token (token exchange): scope `audit.read`, the acting client (`act`) must be the Audit Journey API, and the **person** must hold `audit.view` / `audit.search` - which only auditors have. A plain machine token, the web BFF's token or a person's own token is refused (`DelegatedAuditorAuthorization.cs`). People are shown by LAN ID, resolved from the trail itself. Every read is itself recorded as an `ACCESS` entry (who searched what, when) - search them with `kind=ACCESS`.

## Try it

```sql
-- connected to EwpAuditDb
SELECT sequence, event_type, record_ref, actor_lan_id, initiated_by_lan_id, status, amount, left(entry_hash, 12) AS hash
FROM audit_entries ORDER BY sequence DESC LIMIT 20;
```

Tampering test (as `postgres`; the trigger must be disabled first, which is exactly what a tamperer would have to do):

```sql
UPDATE audit_entries SET amount = 1 WHERE sequence = 1;                -- refused by the trigger
ALTER TABLE audit_entries DISABLE TRIGGER trg_audit_entries_no_change;
UPDATE audit_entries SET amount = 1 WHERE sequence = 1;                -- now it "works"
ALTER TABLE audit_entries ENABLE TRIGGER trg_audit_entries_no_change;
```

Within a minute the API logs `AUDIT CHAIN BROKEN at entry 1`, `/health/ready` shows the `audit-chain` check Degraded, and `/metrics` shows `brokenAtSequence` 1. Recreate the trail afterwards with `EwpAuditDb.sql` (and reset the consumer group to the earliest offset, see ReadMe.txt) - a broken chain cannot be repaired, only re-recorded, which is the point.

## Not yet

- Operations' "Retry release" on a payment publishes no event, so it is not in the trail yet.
- A chain anchor outside the database (e.g. the latest hash periodically written to WORM storage), so even a full re-computation of the chain by an attacker would be detectable.