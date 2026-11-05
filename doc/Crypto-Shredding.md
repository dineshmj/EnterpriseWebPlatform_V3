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
| Audit trail | IDs, people's IDs, outcome, SHA-256 of the message | holds no personal data: nothing to erase, and its chain still verifies; it records each erasure (who asked, who decided, the reason code) and each context's confirmation |
| KYC `kyc_cases.applicant` | the applicant as submitted (name, residential address): **ciphertext** under KYC's own key | KYC's key for the customer is deleted |
| Compliance `compliance_cases.applicant` | the applicant as KYC verified them: **ciphertext** under Compliance's own key | Compliance's key is deleted; the case is never screened again |
| Accounts `account_applications.holder_name` | the name the account is to be opened in: **ciphertext** under Accounts' own key | Accounts' key is deleted; no account is opened from the application |
| Documents Management | the scans (encrypted files) and their file names | the files are **deleted** and the names blanked; the rows (type, size, SHA-256) stay |
| Accounts `accounts`, Partner Products | an ACTIVE customer's name (and, for Digital Gold, date of birth and mobile) | out of scope: only a prospect can be erased, and a prospect has neither |

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

## The erasure itself: one operator asks, another decides

Code: [PersonalDataErasureRequest.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/PersonalDataErasureRequest.cs); screen: Operations → Privacy → **Personal Data Erasure**.

1. **Request.** An operations administrator enters the customer number and a reason **code**: *the customer asked to be forgotten* (`CUSTOMER_REQUEST`) or *the retention period has ended* (`RETENTION_ENDED`). Free text is never asked for, so no personal data reaches the events or the audit trail. The request is refused at once unless the customer qualifies:
   - a **PROSPECT** only: never onboarded, no application in progress. ACTIVE and SUSPENDED customers have accounts the bank must keep; CLOSED will qualify once a closing action exists;
   - **unchanged for the retention period**: `PersonalData:Erasure:RetentionDays`, 2557 days (7 years) in production, 0 on the laptop so the demo can erase a prospect made a minute ago.

   One request per customer may wait at a time; it expires after `RequestValidDays` (7) if nobody decides.
2. **Decision, by ANOTHER person.** A second operations administrator approves (behind an "Erase for good" confirmation) or rejects with remarks. The requester can never decide their own request: the aggregate refuses it, and so does a database constraint (`ck_personal_data_erasure_requests_four_eyes`). Each step needs `personal-data.erasure.request` / `.approve`, comes only through the Operations Journey API, and needs a recent authenticator code.
3. **The shredding.** Approval checks eligibility **again**, then in **one transaction**: deletes the customer's `personal_data_keys` row, clears both blind indexes, marks the customer `personal_data_erased_at`, and writes `CustomerPersonalDataErased` to the Outbox. The ciphertext stays where it is - without the key it is unreadable.
4. **Proof.** The three events (`PersonalDataErasureRequested`, `PersonalDataErasureRejected`, `CustomerPersonalDataErased`, topic `customer.personal-data.erasure`) name the requester as initiator and the second person as decider, with the reason code. The audit trail records them like any decision; the decider's remarks stay in Customer Onboarding.

After that, the branch sees **"Personal data erased"** in place of the name and e-mail, the customer is found by number only, and cannot be changed or onboarded. The customer number, applications and audit entries all stay.

## The other contexts: each erases its own copy, then says so

Code: [PersonalDataErasureForwarder.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/PersonalDataErasureForwarder.cs); each context's `InternalPersonalDataErasuresController`.

KYC, Compliance and Accounts keep their own copy of the applicant (decision 2a), encrypted the same way as Customer Onboarding's but under **their own** master key and per-customer keys (squad autonomy: no shared key, no shared code). Documents Management keeps the scans themselves.

1. **The event.** `CustomerPersonalDataErased` reaches each context's **existing** subscriber (the KYC, Compliance, Account Application Opening and Document Invalidation subscribers also read `customer.personal-data.erasure`). Requested and rejected erasures are skipped: nothing to erase yet.
2. **The erasure.** The subscriber calls its own API's internal endpoint (`POST internal/v1/<context>/personal-data-erasures`, its pinned machine client only):
   - **KYC, Compliance, Accounts** mark every case or application of the customer erased and delete the customer's key **in the same transaction**. The ciphertext stays, unreadable; the screens show *"Personal data erased"*; nobody can act on the case any more, and an erased compliance case is never screened, an erased application never opens an account.
   - **Documents Management** blanks the file names and saves, **then** deletes the content of every document filed under the customer - also those blanked by an earlier try, so a retry after a failure half-way removes what is left. Reading an erased document's content answers *410 Gone*.
   - The API does not check eligibility again: the bank decided the erasure in Customer Onboarding (four eyes); a context only obeys. Called again, it erases nothing new and answers the same.
3. **The confirmation.** Only after the API answered 2xx, the subscriber publishes `PersonalDataCopyErased` (topic `customer.personal-data.erasure.confirmed`: the context, the customer number, the erasure request, how many records it erased and held - nothing personal), and only then commits the message.

**Why no Outbox.** An Outbox makes a database change and an event happen together. Here the context's change is idempotent and the confirmation is sent by the subscriber, which commits the Kafka message last: if it crashes after the erasure but before the confirmation, the message is delivered again, the API answers "done" again, and the confirmation goes out. A confirmation can never be sent for a copy that is still there. The confirmation's ID is derived from the erasure message and the context, so a repeated confirmation is one audit entry.

**Proof.** Every erasure therefore leaves five or more entries in the audit trail: the request, the decision, and one `PersonalDataCopyErased` per context - also from a context that held nothing (*0 erased, 0 held*), so Sarah can see that each one was asked.

**Known limits.**
- As for Customer Onboarding, each context's keys live in its own database: a backup taken before the erasure still holds them (see *Known limit* above), and a disk backup still holds the scans.
- Opened accounts and Partner Products keep an ACTIVE customer's name in clear: they join when a CLOSED customer can be erased.