# EWP V3 tests

| Folder | What | Tooling |
|---|---|---|
| [playwright](playwright) | End-to-end, journey and security tests through the real front ends, in Microsoft Edge | Playwright (TypeScript), run from VS Code |
| [dotnet](dotnet) | Unit tests of the .NET APIs' and the IDP's decisions (Payments, Accounts, Customer Onboarding, KYC, Compliance, Documents Management, Partner Products, IDP) | NUnit, Moq, AwesomeAssertions - own solution `EwpTests.slnx`, not the main one |

## Playwright

### Before the first run

1. **The platform is running**: Visual Studio's multiple start-up profile, `.\ps\run\Start-NodeServices.ps1`, PostgreSQL and Kafka (ReadMe.txt section 1). `Mfa:Enabled` is off (the MFA tests enrol their own user).
2. **Install** (once, and after a dependency change), in `tst\playwright`:
   ```powershell
   pnpm install
   pnpm exec playwright install ffmpeg
   ```
   No browser download is needed: the tests use the installed Microsoft Edge (`channel: 'msedge'`). The second command fetches Playwright's small video encoder (once per machine); without it every test fails at once with "Executable doesn't exist … ffmpeg-win64.exe", because a video of each test is kept when it fails.
3. **Passwords**: copy `tst\playwright\.env.example` to `.env` (git-ignored) and fill in `EWP_PASSWORD_DEFAULT` - or a line per user. The tests sign in only as the demo users seeded in the IDP (`sophie.cs`, `ethan.kyc`, `olivia.compliance`, `jack.accounts`, `emily.payments`, `sarah.audit`, ...).
4. **VS Code**: install the extension **Playwright Test for VS Code** (`ms-playwright.playwright`) and open the repository folder. The tests appear in the **Testing** side bar (the flask icon).

### Running

| How | |
|---|---|
| **VS Code Testing side bar** | ▶ runs a test, a file or a folder; 🐞 debugs it (breakpoints in the test). In the Playwright section at the bottom of the side bar: **Show browser** = headed, **Pick locator** and **Record new** write test code from your clicks. |
| `pnpm test` | everything, headless, in project order |
| `pnpm test:headed` | the same, with visible browser windows |
| `pnpm test:ui` | Playwright's UI mode: a test tree, a time line of every step, the page at each step |
| `pnpm smoke` / `pnpm functional` / `pnpm security` | one kind of test |
| `pnpm report` | the HTML report of the last run (traces, screenshots and videos of failures) |

Every project depends on **smoke**, so running any test first checks that each service is ready; a stopped service is reported by name ("Customer KYC BFF is not answering … start it (Start-NodeServices.ps1)") instead of a screen test timing out.

### Folders

```
playwright\
  tests\
    00-smoke\          every service's readiness probe
    10-seed\           demo records after a database re-create (Camilla Parker, Jason Millers, payments)
    20-functional\     one screen or feature at a time (authentication, onboarding, kyc, ...)
    30-journeys\       several people end to end (agent → KYC → Compliance → Accounts; maker → checker)
    35-resilience\     the journeys while a simulated external system refuses, is down or is slow,
                       or one of the platform's own APIs is cut / hung at the fault proxy
    40-security\       headers, session, csrf, authorization, browser, input, rate-limiting, mfa
  support\
    personas.ts        the demo users, their roles and the menu each role must see
    services.ts        every service's URL and readiness probe
    sign-in.ts         the sign-in as a person does it, and saved sign-ins (.auth\, git-ignored)
    fixtures.ts        signedInAs('sophie.cs') - a signed-in Shell in a context of its own
    pages\             page objects: IDP sign-in / consent / sign-out, the Shell
```

### Several people at once

Each person gets a **browser context** of their own - as isolated as a separate private window (own cookies and sessions) - in the same Edge:

```ts
test('Sophie submits, Ethan sees the KYC case', async ({ signedInAs }) => {
  const sophie = await signedInAs('sophie.cs');
  const ethan  = await signedInAs('ethan.kyc');
  // ...
});
```

A sign-in is saved to `.auth\<user>.json` and reused for 20 minutes (`EWP_SESSION_REUSE_MINUTES`); if the saved session has ended (signed out, BFF state re-created), the person signs in again automatically. The IDP allows about 10 sign-ins a minute from one machine (20 requests to its sign-in pages; a security control). After a database re-create, when every saved sign-in is stale, a run can meet that limit: the sign-in then waits about a minute and tries once more, and the test carries a note saying so.

### MFA tests

`tests\40-security\mfa` runs only with MFA switched on; otherwise it is skipped. The tests play Google Authenticator themselves: they enrol `daniel.ops` from the setup key the IDP shows (kept in `.auth\mfa-daniel.ops.key`) and compute each code. Before running them:

1. Re-create the IDP database (`.\ps\Reset.ps1`, option 1), so `daniel.ops` is not enrolled with a phone. Do not enrol him by hand afterwards.
2. Switch MFA on and shorten the step-up age (Development settings; put them back afterwards):
   - IDP `appsettings.Development.json`: `"Mfa": { "Enabled": true }`
   - Payments, Accounts, Compliance and KYC APIs `appsettings.Development.json`: `"Mfa": { "Enabled": true, "StepUpMaxAgeMinutes": 1 }`
   - Operations Journey API `runnow.bat`: `OPERATIONS_MFA_ENABLED=true`, `OPERATIONS_MFA_MAX_AGE_MINUTES=1`
3. Restart the IDP, those APIs and the Node services.
4. `tst\playwright\.env`: `EWP_MFA_ENABLED=true` and `EWP_STEP_UP_MAX_AGE_MINUTES=1`.
5. Run: `npx playwright test tests/40-security/mfa --project=security` (about 4 minutes: the step-up age is waited out, and each code needs a fresh 30-second step).

With MFA on, the other tests stop at the code page - switch it off again for them.

### Journeys, resilience and security - what they need

- The simulators are switched through their loopback-only `PUT /admin/behaviour` (Down, Refusing, LosingAnswers, a forced screening outcome); every resilience spec sets them back to Healthy afterwards. No console window is ever closed.
- The platform's own APIs are cut or hung at the fault proxy (Toxiproxy, started by `ps	oxiproxyStart-Toxiproxy.ps1`) as `daniel.ops` on **Simulator & Fault Controls** - the real page, Journey API and permission. `withRoute()` (`support/fault-routes.ts`) always opens the route again, and `35-resilience/platform` opens every route before and after the file (Toxiproxy's own reset, loopback only). Restart Visual Studio after pulling: the timing relies on the Development settings (the saga's 10-second reply timer, the subscribers' 10-second retry ceiling).
- `35-resilience` is slow on purpose: retries back off for real, and the four-eyes reconciliation tests wait out the "unconfirmed by the network" alert (2 minutes in Development) - allow about an hour for the project.
- The reconciliation tests need a second operator: `platform.admin` (signs in with `EWP_PASSWORD_DEFAULT` unless `.env` names its own password).
- `35-resilience/partner-products` (Digital Gold deviation runs) is **opt-in**: it stops and starts Kafka (`ps\kafka\Stop-Kafka.ps1` / `Start-Kafka.ps1`) and the Partner Products Worker (`ps\run\Start-NodeServices.ps1 -Only PartnerProductsWorker`) on this PC, so it runs only with `EWP_DEVIATION_RUNS=true` in `.env`. Run it from the same kind of window as Kafka and the Node services: if they were started from an administrator PowerShell, a normal window may not stop them (the file checks this first and says so). Every stop is paired with its start (`finally`), and the file starts both again after its last test. It also needs what `30-journeys/partner-products` needs, `EWP_POSTGRES_PASSWORD` (it reads the Outbox and the payment's status with psql, deletes one test customer's Form VII rows to stand in for an EWP database re-create, and fast-forwards one Outbox message to 9 failed attempts - the 10th is real), and the Temporal CLI (`EWP_TEMPORAL_PATH` when winget did not install it): it reads the workflow's history and forges `paymentSettled` signals. Five runs: Kafka down for minutes during a purchase (the payment ends COMPENSATION_FAILED with its first command parked and is left for operations - one parked Payments message per run, Payments health Degraded until it is resent), the worker restarted while the purchase waits for approval, two forged signals plus a late one, a parked message resent by `daniel.ops` from Platform Health, and GoldAccountLinked. About 30 minutes; two new customers per run.
- `30-journeys/partner-products` (Digital Gold) needs the Partner Bank (`Start-PartnerBank.ps1 -Lan` on the PC, hosts entries for `*.ewp.home.arpa`), Temporal (`ps\temporal\Start-Temporal.ps1`) and the Partner Products Worker. Without the Partner Bank the file is skipped, not failed. Each run onboards a NEW customer: the Partner Bank opens one account per customer and keeps it across EWP database re-creates. The payment-refused test switches the payment network simulator to Refusing and back.
- `40-security/api/token-parity` asks the IDP for a client-credentials token of the KYC case-opening subscriber (a token for ANOTHER API); its development-only secret is read from that subscriber's `appsettings.Development.json` at run time.
- `40-security/api/token-exchange-allow-list` and `worker-internal-endpoints` use real tokens: Sophie's own sign-in token of the Partner Products web BFF (Authorization Code + PKCE, silently through her IDP session; the IDP's redirect is not followed - the code is read from its Location header and never reaches the BFF), the Worker's and the saga reply subscriber's client-credentials tokens, and the delegated tokens the real chain issues. The development-only client secrets are read from `src\IDP\appsettings.Development.json` at run time. Each refusal is paired with one call that IS allowed, so a broken set-up cannot pass as a refusal.
- `40-security/api/out-of-branch-payment` onboards a Sydney customer, then `mia.cs` (Melbourne) sends a payment from that account straight to the Payments BFF: Accounts refuses it (`ACCOUNT_NOT_IN_BRANCH`), the payment ends REJECTED.
- `40-security/audit/audit-tamper-evidence` plays a database administrator: it needs `EWP_POSTGRES_PASSWORD` (the PostgreSQL superuser, as for `Reset.ps1`) in `.env` - skipped without it. It edits one audit entry (trigger switched off for one transaction) and moves one checkpoint file out of `src\Microservices\Audit\API\AuditAnchors`, waits for the Audit API's readiness to report each, and puts both back at once (`finally`). Restart the Audit API after pulling (the checkpoint check changed); it must have run at least one minute so it has seen the checkpoints. About 6 minutes.

### Conventions

- Arrange / Act / Assert sections in every test; multi-step walk-throughs use `test.step` so each step is named in the report.
- No fixed sleeps: anything that happens through Kafka is awaited with `expect.poll` / `toPass` and a time limit.
- The tests share one platform and its data, so they run one at a time (`workers: 1`). Records created by tests carry unique names; the seed scenarios create the fixed demo customers.
- Failed sign-ins use an unknown username, never a real user's wrong password (that would count towards the user's lockout).

## .NET unit tests

`tst\dotnet\EwpTests.slnx` - its own solution, so the main one does not grow. No running platform, database or Kafka is needed.

| Project | What it checks |
|---|---|
| `Payments.Api.Tests` | the payment's rules and approval tier; the saga - reserve first, replies matched to the current command (CausationId), approval limits and maker-checker, network retries with doubling waits, the unknown outcome confirmed with the network (never released blind), four-eyes reconciliation, reply timeouts, stuck compensation; one payment per Idempotency-Key; staff authorization (branch, audit permission only through the Audit Journey API) |
| `Accounts.Api.Tests` | when funds may be held (branch first, owner, currency, available funds); idempotent reserve / settle / release, a release that overtakes its reservation; separation of duties on account opening; bounded core-banking retries; the account officer policy |
| `CustomerOnboarding.Api.Tests` | the onboarding saga's state: submitted once with its evidence (each document once); KYC, Compliance and Accounts facts that arrive repeated or out of order (a later fact applies the steps still outstanding, a stale one changes nothing); a rejection names its stage and the evidence, a failed account opening compensates first. The customer: the name is verified identity once onboarding starts, one onboarding at a time, one online identity only for an ACTIVE customer with the e-mail on record (never taken over). Also the BIAN `/v2` PartyReferenceDataDirectory mapping against BIAN's own schema |
| `CustomerKyc.Api.Tests` | the KYC case: opened only with two different evidence documents; the initiator never takes or decides it (an unknown initiator fails closed); the first decision assigns the case and only the assignee decides or releases it; the status derived from both stages in either order (one rejection ends it); each stage decided once; remarks required to reject; the KYC officer policy (role, both permissions, department, clearance 3) |
| `Compliance.Api.Tests` | the compliance case: a failed screening is never "clear" (stays in SCREENING, retried, nobody can decide it); the verdict sets the risk and the clearance to approve (low 3, medium 4, high 5 - one level short is refused); neither the initiator nor either KYC officer of the application may act (four eyes across contexts); hold and release, reasons required; only the assignee acts; the compliance officer policy |
| `DocumentsManagement.Api.Tests` | evidence retained, never deleted: removable only while AVAILABLE, ATTACHED is a KYC record, a rejection INVALIDATES it with the reason; repeated commands change nothing; the branch fixed at upload; the content type from the file's own bytes (PDF, PNG, JPEG), a mismatch with the declared type refused; file names lose any path and unsafe characters. Also the BIAN `/v2` DocumentDirectory mapping |
| `PartnerProducts.Api.Tests` | the Partner Bank adapter (`PartnerBankClient`) against a **WireMock.Net** Partner Bank inside the test: EWP's token (client credentials) on every instruction; the instruction a JWT signed with EWP's key that a verifier holding only the public key accepts (ES256, typ, issuer, audience, 5 minutes, jti); a lost (late) answer and the resend carry the SAME jti; 400 / 404 / 409 are final refusals with the Partner Bank's own reason, 5xx / unreachable / our token refused are "unavailable" (never a refusal, no internal text passed on); a rejected token is fetched anew; no token, no instruction. Not covered: mutual TLS on the wire (the Partner Bank's own check). Also the Digital Gold purchase (`GoldPurchase`): a slip only for an open account, signed today, with the mobile on record and the signature checked; the instruction ID fixed at the first send; the payment exactly the slip to the settlement account, one payment per order; PAID only with its own payment (key, amount, payee), repeated reports change nothing; allocated only when paid; cancelled only while unpaid |
| `IDP.Tests` | the one-time MFA codes (`MfaService`, review fix R2) on an in-memory **SQLite** database - the claim is a conditional UPDATE, which EF Core's in-memory provider cannot run: a code accepted once (also the enrolment code), an older code refused, a recovery code once, five wrong codes lock for 15 minutes, a right code resets the count; the secret stored encrypted, recovery codes as hashes. The customers' sign-in (My EWP): a one-time code accepted once, five tries then spent (even the right code), expired after five minutes, only a salted hash stored; five wrong passwords lock for 15 minutes and the lock ends after them, a wrong code counts like a wrong password, a second registration for the same customer is refused, the password rule (length, not the customer number, both entries equal). SQLite cannot compare `DateTimeOffset` in SQL, so `CustomerTestDatabase` stores them as UTC ticks (test only) |

### Running

The APIs are usually running while you work, and they lock their own `bin` folders - so build the tests somewhere else:

```powershell
dotnet test tst\dotnet\EwpTests.slnx --artifacts-path $env:TEMP\ewp-tests
```

In Visual Studio: open `EwpTests.slnx` in a second instance and use Test Explorer (stop the target APIs and the IDP first, or the build cannot overwrite their DLLs).

### Conventions

- Plain NUnit in Given / When / Then style: method names `Given_<situation>_When_<action>_Then_<outcome>`, and `// Given`, `// When`, `// Then` sections.
- `[Category]` uses the Gherkin tags of BSS-Requirements.md: `Happy`, `Boundary`, `Security`, `SoD`, `Resilience`, `Compensation`.
- File and class: `<Class>_<Method>_<Happy|Edge>_<Case>`; several inputs through `[TestCase]`.
- Decisions, not coverage: aggregates, value objects, policies, authorization handlers and the decision branches of command handlers. `Program.cs`, DTOs, EF mappings, thin controllers and hosted-service plumbing are not unit-tested.
- Test data comes from small helpers (`Support\*TestData.cs`) that build aggregates through their own public factories and methods - the domain has no public setters, so NBuilder is not used.
- An external system is played by **WireMock.Net** (`Support\PartnerBankStub.cs`): a real HTTP server inside the test, started and stopped per test; the REAL adapter talks to it, each test stages answers (status, body, delay) and reads what arrived. Throw-away keys per run, never `.ewp-pki`.

## Node.js tests (Jest + WireMock standalone)

The customer's NestJS pattern: a Journey API tested against **WireMock standalone** (the Java server) playing its neighbours, set up over WireMock's admin API from TypeScript.

| Project | What it checks |
|---|---|
| `src\Microservices\PartnerProducts\JourneyApi` (`test\`) | `DomainApiClient` + `TokenExchangeService` with WireMock as the IDP's token endpoint AND the Partner Products Domain API: the person's token exchanged (RFC 8693) for exactly one scope, one exchange per scope, the exchanged token sent (never the person's); IDP refusal = 403 and no Domain API call; the domain's own 400 / 409 / 503 reasons reach the counter; 500 and 403 become plain messages (no internal text, CWE-209); unreachable = "unavailable" |

Once: `.\ps\wiremock\Install-WireMock.ps1` (WireMock 3.13.2 into `%LOCALAPPDATA%\EWP\wiremock`, SHA-256 checked; needs Java - Kafka's is enough). Then, in the project folder: `pnpm test`. Jest starts WireMock on 127.0.0.1 and a free port (at most 128 MB) for the run and stops it at the end - nothing keeps running, the platform need not be up.