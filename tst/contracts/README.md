# Consumer-driven contracts (Pact)

**In short:** when one service calls another, or reads its Kafka events, the caller (the *consumer*) writes down exactly what it sends and what it needs back. That note is a *contract*: a JSON file in [pacts/](pacts). The service that answers or publishes (the *provider*) replays every contract that names it in its own tests. If a provider renames a field, drops one, changes its type or answers a refusal with another status, its test fails in the pull request that makes the change - before anything is deployed, and without starting the other service.

No Pact Broker, no extra process: the files are committed, reviewed as diffs, and checked by CI.

## What is covered today: the Payments saga loop

| # | Consumer | Provider | Kind | Contract file |
|---|---|---|---|---|
| 1 | `AccountsCommandSubscriber` | Payments API | Kafka: `ReserveFunds` / `SettleFunds` / `ReleaseFunds` on `accounts.commands` | `accounts-command-subscriber-payments-api.json` |
| 2 | `AccountsCommandSubscriber` | Accounts API | HTTP: `POST /internal/v1/accounts/funds/commands` (200, and 409 for a settle with nothing held) | `accounts-command-subscriber-accounts-api.json` |
| 3 | `PaymentsSagaReplySubscriber` | Accounts API | Kafka: `FundsReserved` / `FundsReservationFailed` / `FundsSettled` / `FundsReleased` on `accounts.funds.replies` | `payments-saga-reply-subscriber-accounts-api.json` |
| 4 | `PaymentsSagaReplySubscriber` | Payments API | HTTP: `POST /internal/v1/payment-sagas/replies` (200, and 400 for an unknown payment) | `payments-saga-reply-subscriber-payments-api.json` |
| 5 | Partner Products Worker (Node) | Payments API | Kafka: `PaymentCompleted` / `PaymentFailed` on `payments.payment.events` | `partner-products-worker-payments-api.json` |
| 6 | Partner Products Worker (Node) | Payments API | HTTP: `GET /internal/v1/payments/by-idempotency-key/{ref}` (200 COMPLETED, 404 none) | same file (Pact V4 holds both) |

The other consumers (the onboarding chain, Notifications, Audit, the web BFFs, the Journey APIs) follow the same pattern when they are added.

## How it works

```text
 consumer test (C# NUnit / TS Jest)                     provider test (C# NUnit)
 ─────────────────────────────────                      ───────────────────────
 REAL courier / reader / activity                       REAL API on Kestrel (WebApplicationFactory)
   ↕ Pact mock server (HTTP)                              ← Pact verifier replays each HTTP request
   ← Pact example message (Kafka)                         ← POST /provider-states sets the mocks
 writes pacts/<consumer>-<provider>.json  ──commit──▶   REAL Outbox mapper builds each Kafka message
```

- **Only what the consumer reads is stated.** The courier reads eight fields of a funds command, so the contract names those eight; Payments may add fields freely. Matchers say what may vary (a GUID must be a GUID, an amount a number); an exact value is pinned only where the consumer branches on it (the worker allocates gold only on `status: "COMPLETED"`).
- **Status codes are part of the contract.** The couriers decide from the status alone: 2xx processed, 400 / 409 dead-lettered, 5xx retried. A refusal that became a 500 would retry a bad message for ever - the contract catches it.
- **Provider states** are the consumers' words (`Given("funds are held for the payment")`). The provider test turns each into mocks of its application ports (repositories, Inbox, unit of work), built through the aggregates' own methods. An unknown state fails the verification.
- **Kafka messages** are not fetched from Kafka: the provider test builds each one with the REAL Outbox mapper (`PaymentsMessageMapper`, `FundsIntegrationEventMapper`) from a real aggregate - exactly what the relay would publish.
- **Identity** in the provider test: a test scheme signs in the consumer's machine client (its pinned `client_id` and scope), so the real authorization policies still run. Token validation and DPoP are not what a contract is about - the security tests prove them.

| Part | Where |
|---|---|
| C# consumers (the two couriers) | [tst/dotnet/AsyncWorkflows.Subscribers.ContractTests](../dotnet/AsyncWorkflows.Subscribers.ContractTests) |
| Node consumer (the worker) | [src/Microservices/PartnerProducts/Worker/test/contracts](../../src/Microservices/PartnerProducts/Worker/test/contracts) |
| Payments provider | [tst/dotnet/Payments.Api.Tests/Contracts](../dotnet/Payments.Api.Tests/Contracts) |
| Accounts provider | [tst/dotnet/Accounts.Api.Tests/Contracts](../dotnet/Accounts.Api.Tests/Contracts) |
| Shared (file names, the provider host) | [tst/dotnet/Shared/ContractFiles.cs](../dotnet/Shared/ContractFiles.cs), [ContractProviderHost.cs](../dotnet/Shared/ContractProviderHost.cs) |
| CI drift check | [.github/scripts/contracts-up-to-date.sh](../../.github/scripts/contracts-up-to-date.sh) |

## Running

Nothing needs to be running - no platform, database or Kafka.

```powershell
# all .NET tests, consumers and providers (the APIs lock their own bin folders, so build elsewhere)
dotnet test tst\dotnet\EwpTests.slnx --artifacts-path $env:TEMP\ewp-tests

# only the contract tests
dotnet test tst\dotnet\EwpTests.slnx --artifacts-path $env:TEMP\ewp-tests --filter TestCategory=Contract

# the worker (in src\Microservices\PartnerProducts\Worker)
pnpm test
```

## Changing a contract

1. **A consumer needs something new** (a field, a status): change the consumer and its contract test, run the consumer's tests. They rewrite their files in `pacts/` - **only when every contract test of that project ran and passed** (a filtered or failed run changes nothing; the files are moved in with one atomic replace, so a provider test running at the same time never reads half a file). Commit the changed file with the change.
2. **The provider's tests now fail** until the provider gives what the contract asks - in the same pull request, or the provider's first.
3. **CI** ("Contracts up to date") fails when the files the consumer tests write differ from the committed ones: a consumer's new expectation can never be left uncommitted while the providers are checked against the old one.

A provider change that breaks a consumer fails like this (the Accounts API with `HoldStatus` renamed and the 409 changed to 422):

```text
1) Verifying a pact between accounts-command-subscriber and accounts-api Given no funds are held for the payment
   - a SettleFunds command for a payment Accounts holds nothing for
   1.1) has status code 409
        expected 409 but was 422
2) Verifying a pact between payments-saga-reply-subscriber and accounts-api - a FundsReleased reply
   2.1) has a matching body
        $.Payload -> Actual map is missing the following keys: HoldStatus
```

## Adding a consumer

1. Name it (lower-case, hyphens) in [ContractFiles.cs](../dotnet/Shared/ContractFiles.cs) and use the same spelling in TypeScript.
2. Write its consumer test with the REAL client or reader; write the files into a work folder and publish them only after a complete, green run (copy [PactFilesSetUp.cs](../dotnet/AsyncWorkflows.Subscribers.ContractTests/PactFilesSetUp.cs) or the worker's spec).
3. In the provider's verification test, add its states (`*ProviderStates.Set`) and messages, and its file to the expected list - a contract that disappears fails the provider test too.

## Notes

- **Pact telemetry is off** (`PACT_DO_NOT_TRACK=true`, set by the tests themselves): Pact's native core otherwise reports usage to the Pact Foundation. The worker's `@scarf/scarf` install script (download analytics) is never run (`pnpm-workspace.yaml`).
- **Versions:** PactNet 5.0.1 (.NET), `@pact-foundation/pact` 17.1.4 (Node) - both write Pact specification V4. Test-only dependencies: nothing here is deployed.
- **Why no Pact Broker:** a broker adds `can-i-deploy` across independently versioned services. Here every service is released from one repository, so the committed file plus the CI drift check gives the same guarantee without another server. A broker becomes worth it when squads move to repositories of their own.