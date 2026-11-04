# Crypto-shredding: erasing personal data without touching the audit trail

A bank has two duties that pull against each other:

- **Keep the record.** The audit trail is append-only and hash-chained ([EwpAuditDb.sql](../src/Microservices/Audit/API/AuditDb/EwpAuditDb.sql)). Nobody may change or delete an entry, and records must be kept for the retention period (AML/CTF: 7 years after the relationship ends).
- **Erase the person.** Once that period ends, the person's personal data must be erased (GDPR Art. 17, APP 11.2): every copy, backups included.

**Crypto-shredding** settles this. Each customer's personal data is encrypted with that customer's own key. To erase the person, destroy the key: every copy becomes unreadable at once, and nothing append-only has to change.

## What is where

| Copy | Holds | How erasure reaches it |
|---|---|---|
| Customer Onboarding `customers`, `customer_addresses` | name, e-mail, phone, address lines: **ciphertext** | the customer's key is deleted: unreadable |
| Database backups of `EwpCustomerDb` | the same ciphertext, **and the key rows of that moment** | see *Known limit* below |
| `personal_data_keys` | each customer's data key, **wrapped** by the master key | the row is deleted: that is the erasure |
| Kafka topics | events; `OnboardingApplicationSubmitted` carries the applicant's name and address in clear | every topic keeps a message **7 days** (stated, not a default) |
| Customer Onboarding Outbox | the published payloads | the relay **blanks** a payload 7 days after publishing; the row stays |
| Audit trail | IDs, people's IDs, outcome, SHA-256 of the message | holds no personal data: nothing to erase, and its chain still verifies |
| KYC, Compliance, Accounts, Partner Products, Documents | their own copies | **next PR:** each context's own key, shredded on the erasure event |

## How the encryption works

Code: [PersonalDataCipher.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Privacy/PersonalDataCipher.cs) and [CustomerPersonalData.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Privacy/CustomerPersonalData.cs).

- **Two master keys,** from configuration `PersonalData` (Key Vault in the cloud demo, development values on the laptop):
  - `MasterKey` wraps (encrypts) every customer's data key;
  - `BlindIndexKey` keys the exact-match lookups.

  If either key is missing, shorter than 32 bytes, or the two are the same, the API refuses to start.
- **One data key per customer.** It is made with the customer: 32 random bytes, stored only wrapped (AES-256-GCM under the master key).
- **Each personal field** is AES-256-GCM under the customer's data key, with the customer number as *associated data*. A value copied onto another customer's row fails to decrypt; it never comes out as garbled text. The stored form is `v1.<base64 of nonce | ciphertext | tag>`, and the database refuses any personal field without the `v1.` prefix.
- **Where it happens:**
  - The unit of work (`CustomerDbContext.SaveChangesAsync`) encrypts every added or changed customer before anything is written.
  - The repository decrypts an aggregate when it loads it.
  - Queries decrypt only the customers on their page, through the `ICustomerPersonalData` port. The Application layer never sees ciphertext.
- **Search.** SQL cannot look inside ciphertext:
  - the customer number still matches in part;
  - an e-mail address or surname matches only **exactly** (ignoring case and outer spaces), through an HMAC-SHA256 blind index.

  On its own, a blind index only confirms a value someone already knows, so erasure clears it as well.
- **What stays readable:** city, state, postcode and country. On their own they identify nobody, and the branch scope filters customers by city in SQL.

## Why the applicant still travels in clear

KYC needs the applicant as submitted, to check the evidence against it. The alternative was a *thin event*: the KYC subscriber would fetch the applicant from Customer Onboarding when the event arrives. It was weighed and not taken, for three reasons:

- it would put a synchronous call inside an asynchronous choreography, so KYC case opening would fail whenever Customer Onboarding is down;
- it would need a second machine client for the subscriber;
- KYC would read whatever is stored at consumption time, not the fact as submitted.

Instead, **every clear copy has an end**, far shorter than any retention period after which erasure can happen:

- **Kafka:** `retention.ms` = 7 days on every topic, dead-letter topics included. On the laptop this is set by [Setup-KafkaSecurity.ps1](../ps/kafka/Setup-KafkaSecurity.ps1), which also applies it to existing topics; in the cloud by `kafka.retentionMs` in [ewp-infra values](../deploy/helm/ewp-infra/values.yaml).
- **Outbox:** the relay replaces a published payload with `{}` after `PayloadRetentionDays` (7) and sets `payload_redacted_at`. The row is kept, because the Outbox is never deleted: it remains evidence of what was published and when, and the audit trail keeps the payload's SHA-256. Unpublished and parked rows are never touched.

## Known limit: the keys live in the same database

In this PoC, `personal_data_keys` sits in `EwpCustomerDb`, next to the data. A backup taken **before** an erasure therefore holds the customer's key as well, and restoring that backup would make their data readable again. Production closes this gap in one of two ways:

- **Keep the keys in a separate key store with a short backup retention:** a separate database, or an HSM / Key Vault with one key per customer. Erasing a key then outlives every data backup within days.
- **Re-apply erasures after every restore:** the erasure log (the audit trail's erasure entries) lists the customer numbers whose keys must be deleted again before the restored database goes live.

The master key alone does not help here: it decrypts every wrapped key in the backup.

## Next: the erasure itself

The next PR adds:

- **The erasure workflow.** An Operations user requests it and a second person approves it. It is refused while retention still runs (only PROSPECT or CLOSED customers, unchanged for the retention period).
- **The shredding itself:** the key row and the blind indexes are deleted.
- **A `CustomerPersonalDataErased` event.** Each context shreds its own copy and acknowledges, and the audit trail records the request and every acknowledgement (IDs only).

After that, screens show "Personal data erased", and the customer number, accounts, amounts and audit entries all stay.