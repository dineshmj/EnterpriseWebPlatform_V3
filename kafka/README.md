# Local Kafka for EWP V3

The platform uses a single-node Kafka broker in KRaft mode, installed natively at `C:\Kafka` (no Docker needed). This folder secures that broker and adds a read-only browser for it.

| File | Purpose |
|---|---|
| [Setup-KafkaSecurity.ps1](../ps/kafka/Setup-KafkaSecurity.ps1) | TLS (`SASL_SSL`), SCRAM-SHA-512 authentication, one Kafka user per deployable, deny-by-default ACLs, explicit topics, no auto-creation |
| [Reset-KafkaRecords.ps1](../ps/kafka/Reset-KafkaRecords.ps1) | Empties every topic (deletes all records up to each topic's end) while the topics, ACLs and consumer groups stay - with `ps\database\Initialize-EwpDatabases.ps1`, a clean slate. Asks first; Kafka running, EWP services stopped |
| [Stop-Kafka.ps1](../ps/kafka/Stop-Kafka.ps1) | Stops the broker without knowing its PID (for when Ctrl+C is not possible, e.g. under PowerShell ISE), waits for the ports to free, and clears the read-only flag on metadata snapshots that otherwise breaks the next start on Windows |
| [kafka-ui.yml](kafka-ui.yml) | Configuration for Kafka UI: localhost only, login form, TLS to the broker, read-only Kafka user, read-only cluster |

## 1. Secure the broker (once)

Run the phases in order from the repository root (PowerShell). Each phase checks its precondition and is safe to re-run.

```powershell
# Once: the local PKI issues the broker's certificate (kafka-broker.pfx) and writes ewp-trust-root.pem
.\ps\pki\New-EwpLocalPki.ps1

# Kafka RUNNING (still PLAINTEXT): users, topics, new consumer groups
.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare

# Stop Kafka (Ctrl+C in its window), then: SASL_SSL listener + authorizer (server.properties is backed up first)
.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Secure

# Start Kafka (C:\Kafka\StartKafka.bat), then: least-privilege ACLs
.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Acls
```

**Adding a component to an already-secured broker** (e.g. a new bounded context): with Kafka running, run `-Phase Prepare` (it detects `config\admin.properties` and authenticates as admin) and then `-Phase Acls`. The Secure phase is not needed again.

**Moving a broker from `SASL_PLAINTEXT` to `SASL_SSL`** (set up before B8): run `New-EwpLocalPki.ps1`, stop Kafka, run `-Phase Secure`, start Kafka. Users, topics, ACLs and offsets stay; restart every EWP component afterwards (they now connect with TLS).

To undo: stop Kafka and run `.\ps\kafka\Setup-KafkaSecurity.ps1 -Revert` (restores the backed-up `server.properties`), then set `Kafka:SecurityProtocol` in the components (and the Worker, Kafka UI and `config\admin.properties`) to match.

### Who may do what

| Kafka user | Used by | Allowed |
|---|---|---|
| `ewp-co-outbox-relay` | CustomerOutboxPublisher | Write `customer.created`, `onboarding.application.submitted`, `onboarding.application.status.changed`, `onboarding.application.rejected` |
| `ewp-kyc-api` | Customer KYC API (in-process relay) | Write `kyc.*` (prefixed) |
| `ewp-compliance-api` | Compliance API (in-process relay) | Write `compliance.case.*` (prefixed) |
| `ewp-compliance-case-opening-subscriber` | ComplianceCaseOpeningSubscriber | Read `kyc.case.approved` and group `compliance.case-opening-subscriber`; write `compliance.case-opening-subscriber.dlq` |
| `ewp-accounts-api` | Accounts API (in-process relay) | Write `accounts.application.*` and `accounts.account.*` (prefixed), and `accounts.funds.replies` (replies to the Payments saga) |
| `ewp-accounts-application-opening-subscriber` | AccountApplicationOpeningSubscriber | Read `compliance.case.approved` and group `accounts.application-opening-subscriber`; write `accounts.application-opening-subscriber.dlq` |
| `ewp-payments-api` | Payments API (in-process relay) | Write `accounts.commands` (the saga's commands) and `payments.payment.events` |
| `ewp-accounts-command-subscriber` | AccountsCommandSubscriber | Read `accounts.commands` and group `accounts.command-subscriber`; write `accounts.command-subscriber.dlq` |
| `ewp-payments-saga-reply-subscriber` | PaymentsSagaReplySubscriber | Read `accounts.funds.replies` and group `payments.saga-reply-subscriber`; write `payments.saga-reply-subscriber.dlq` |
| `ewp-audit-api` | Audit API (in-process trail subscriber) | **Read only**: every business event topic (onboarding, KYC including the stage decisions, Compliance, Accounts, `accounts.funds.replies`, `payments.payment.events`) and group `audit.trail-subscriber` (starts at the **earliest** offset, so the trail records the history); write `audit.trail-subscriber.dlq` |
| `ewp-notifications-subscriber` | NotificationsSubscriber | Read `kyc.case.created/approved/rejected`, `compliance.case.screened/approved/rejected`, `accounts.application.created/rejected`, `accounts.account.opened/opening.failed`, `payments.payment.events` and group `notifications.subscriber` (starts at latest); write `notifications.subscriber.dlq` |
| `ewp-dm-invalidation-subscriber` | DocumentInvalidationSubscriber | Read `onboarding.application.submitted`, `onboarding.application.rejected` and group `documents-management.invalidation-subscriber`; write `documents-management.invalidation-subscriber.dlq` |
| `ewp-kyc-case-opening-subscriber` | KycCaseOpeningSubscriber | Read `onboarding.application.submitted` and group `customer-kyc.case-opening-subscriber`; write `customer-kyc.case-opening-subscriber.dlq` |
| `ewp-onboarding-outcome-subscriber` | OnboardingOutcomeSubscriber | Read `kyc.case.created/approved/rejected`, `compliance.case.created/approved/rejected`, `accounts.application.created/rejected`, `accounts.account.opened`, `accounts.account.opening.failed` and group `customer-onboarding.outcome-subscriber`; write `customer-onboarding.outcome-subscriber.dlq` |
| `ewp-kafka-ui` | Kafka UI | Read and describe everything; no writes |
| `admin` | The broker itself and the CLI tools | Super user |

Everything else is denied (`allow.everyone.if.no.acl.found=false`). For example, only the Customer Onboarding relay can publish an application submission. A forged `onboarding.application.submitted` message, which could otherwise claim a false initiator and get around separation of duties, is rejected by the broker.

Each component reads its own password from `appsettings.Development.json` (`Kafka:SaslPassword`). Outside Development, it comes from the environment (`Kafka__SaslPassword`) or a secret store.

### Using the CLI tools afterwards

Every tool now needs the admin credentials:

```powershell
C:\Kafka\bin\windows\kafka-topics.bat --bootstrap-server localhost:9092 --command-config C:\Kafka\config\admin.properties --list
```

Update `C:\Kafka\ListTopics.bat` the same way.

**TLS.** The client listener is `SASL_SSL`: TLS 1.2 or 1.3 with the `kafka-broker` certificate (issued by the EWP local trust root for `localhost` and `127.0.0.1` only), then SCRAM inside the tunnel. Every client checks the certificate against `ewp-trust-root.pem` and checks the host name: the .NET components (`Kafka:SslCaLocation`), the Partner Products Worker (`KAFKA_SSL_CA_LOCATION`), Kafka UI and the CLI tools (`config\admin.properties`). Clients do not present certificates; SCRAM identifies them.

**Local versus production.** The CONTROLLER listener stays PLAINTEXT, bound to localhost: the broker's own requests to the controller arrive as `User:ANONYMOUS`, which is therefore a super user. Development writes the keystore and admin passwords into `server.properties`. Production secures every listener (TLS or mTLS), uses certificates from the bank's CA with rotation, a managed secret store, and no anonymous super user.

## 2. Kafka UI (optional)

[Kafka UI](https://github.com/kafbat/kafka-ui) (the maintained kafbat fork) is a browser for topics, messages, consumer groups and their lag. It runs as a single Java application, using the Java runtime you already have.

1. **Download.** From the [releases page](https://github.com/kafbat/kafka-ui/releases), download the latest `api-v*.jar` file (for example `api-v1.4.2.jar`) into a folder such as `C:\KafkaUI`.
2. **Configure.** Copy [kafka-ui.yml](kafka-ui.yml) into the same folder.
3. **Run** (in that folder):

   ```powershell
   java --add-opens java.rmi/javax.rmi.ssl=ALL-UNNAMED -Dspring.config.additional-location=kafka-ui.yml -jar .\api-v1.4.2.jar
   ```

4. **Browse** to <http://localhost:8085> and sign in as `ewp` / `ewp-kafka-ui-login-dev`.

Run it **after** securing the broker (section 1): the configuration already uses the read-only `ewp-kafka-ui` Kafka user. The UI is read-only on purpose. To create or delete topics, use the CLI tools with `admin.properties`.