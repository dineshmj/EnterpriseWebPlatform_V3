# 12 — Kafka broker and ACLs

Deployable: the local Kafka broker (`C:\Kafka`, KRaft), set up by `ps/kafka/Setup-KafkaSecurity.ps1` (phases Prepare → Secure → Acls)

## 1. Authentication

- [x] **SASL/SCRAM-SHA-512 on the client listener:** one user per deployable, each with its own password. *Because* a client without credentials cannot connect at all.
- [x] **Both listeners are bound to `localhost` only.**
- [x] **Every client fails closed:** without SASL settings it does not start. → `src/AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs`
- [x] **TLS on the client listener (`SASL_SSL`):** the `kafka-broker` certificate from `ps/pki/New-EwpLocalPki.ps1` (localhost / 127.0.0.1 only), TLS 1.2+. Every client (.NET `Kafka:SslCaLocation`, the Partner Products Worker, Kafka UI, the CLI tools) checks it against the EWP trust root and checks the host name. *Because* SCRAM protects the password but not the events themselves.

## 2. Authorization (deny by default)

- [x] **`StandardAuthorizer` with `allow.everyone.if.no.acl.found = false`:** nothing is allowed unless granted.
- [x] **Producers may write only their own topics:**

  | Producer | Topics |
  | --- | --- |
  | CO relay | its four onboarding topics |
  | KYC API | `kyc.*` |
  | Compliance API | `compliance.case.*` |
  | Accounts API | `accounts.application.*`, `accounts.account.*`, `accounts.funds.replies` |
  | Payments API | `accounts.commands` (its saga's commands to Accounts) and `payments.payment.events` |
- [x] **Consumers may read only their topics and their group,** and write only their own dead-letter topic.
- [x] **The Audit API may read every business topic** and write only its dead-letter topic.
- [x] **Kafka UI is read-only:** `Read`, `Describe` and `DescribeConfigs`.
- [x] **Topics are created explicitly,** and `auto.create.topics.enable = false`. *Because* a typo must not create a new, unprotected topic.

## 3. Operations

- [x] **`Start-Kafka.ps1` / `Stop-Kafka.ps1`** run the broker in its own console window; `Reset-KafkaRecords.ps1` empties the topics (Reset option 2).
- [x] **The setup can be undone:** a backup of `server.properties` is kept and can be restored.

## 4. Production (open)

- [ ] **Broker certificate from the bank's CA, with rotation,** and the keystore password from a secret store (locally it is written into `server.properties`).
- [ ] **No `User:ANONYMOUS` in `super.users`.** Locally it is there because the controller listener is PLAINTEXT on localhost; in production the controller listener is secured.
- [ ] **Replication factor of 3 or more** and `min.insync.replicas` of 2 or more. Locally there is one broker with replication factor 1.
- [ ] **Credentials in a secret store,** with rotation.