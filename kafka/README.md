# Local Kafka for EWP V3

The platform uses a single-node Kafka broker in KRaft mode, installed natively at `C:\Kafka` (no Docker needed). This folder secures that broker and adds a read-only browser for it.

| File | Purpose |
|---|---|
| [Setup-KafkaSecurity.ps1](Setup-KafkaSecurity.ps1) | SCRAM-SHA-512 authentication, one Kafka user per deployable, deny-by-default ACLs, explicit topics, no auto-creation |
| [Stop-Kafka.ps1](Stop-Kafka.ps1) | Stops the broker without knowing its PID (for when Ctrl+C is not possible, e.g. under PowerShell ISE), waits for the ports to free, and clears the read-only flag on metadata snapshots that otherwise breaks the next start on Windows |
| [kafka-ui.yml](kafka-ui.yml) | Configuration for Kafka UI: localhost only, login form, read-only Kafka user, read-only cluster |

## 1. Secure the broker (once)

Run the phases in order from the repository root (PowerShell). Each phase checks its precondition and is safe to re-run.

```powershell
# Kafka RUNNING (still PLAINTEXT): users, topics, new consumer groups
.\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare

# Stop Kafka (Ctrl+C in its window), then: SASL listener + authorizer (server.properties is backed up first)
.\kafka\Setup-KafkaSecurity.ps1 -Phase Secure

# Start Kafka (C:\Kafka\StartKafka.bat), then: least-privilege ACLs
.\kafka\Setup-KafkaSecurity.ps1 -Phase Acls
```

To undo: stop Kafka and run `.\kafka\Setup-KafkaSecurity.ps1 -Revert` (restores the backed-up `server.properties`), then set `Kafka:SecurityProtocol` back to `Plaintext` in the components.

### Who may do what

| Kafka user | Used by | Allowed |
|---|---|---|
| `ewp-co-outbox-relay` | CustomerOutboxPublisher | Write `customer.created`, `onboarding.application.submitted`, `onboarding.application.status.changed` |
| `ewp-kyc-api` | Customer KYC API (in-process relay) | Write `kyc.*` (prefixed) |
| `ewp-kyc-case-opening-subscriber` | KycCaseOpeningSubscriber | Read `onboarding.application.submitted` and group `customer-kyc.case-opening-subscriber`; write `customer-kyc.case-opening-subscriber.dlq` |
| `ewp-onboarding-outcome-subscriber` | OnboardingOutcomeSubscriber | Read `kyc.case.created/approved/rejected` and group `customer-onboarding.outcome-subscriber`; write `customer-onboarding.outcome-subscriber.dlq` |
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

**Local versus production.** Local development uses `SASL_PLAINTEXT` on localhost. SCRAM never sends the password itself, but the traffic is not encrypted. The CONTROLLER listener stays PLAINTEXT, bound to localhost: the broker's own requests to the controller arrive as `User:ANONYMOUS`, which is therefore a super user. Production uses `SASL_SSL` (or mTLS) on every listener, a managed secret store, and no anonymous super user.

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
