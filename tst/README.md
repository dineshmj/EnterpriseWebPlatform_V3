# EWP V3 tests

| Folder | What | Tooling |
|---|---|---|
| [playwright](playwright) | End-to-end, journey and security tests through the real front ends, in Microsoft Edge | Playwright (TypeScript), run from VS Code |
| [dotnet](dotnet) | Unit tests of the .NET APIs' decisions (Payments, Accounts so far) | NUnit, Moq, AwesomeAssertions - own solution `EwpTests.slnx`, not the main one |

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

A sign-in is saved to `.auth\<user>.json` and reused for 20 minutes (`EWP_SESSION_REUSE_MINUTES`); if the saved session has ended (signed out, BFF state re-created), the person signs in again automatically.

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
- The platform's own APIs are cut or hung at the fault proxy (Toxiproxy, started by `Start-NodeServices.ps1`) as `daniel.ops` on **Simulator & Fault Controls** - the real page, Journey API and permission. `withRoute()` (`support/fault-routes.ts`) always opens the route again, and `35-resilience/platform` opens every route before and after the file (Toxiproxy's own reset, loopback only). Restart Visual Studio after pulling: the timing relies on the Development settings (the saga's 10-second reply timer, the subscribers' 10-second retry ceiling).
- `35-resilience` is slow on purpose: retries back off for real, and the four-eyes reconciliation tests wait out the "unconfirmed by the network" alert (2 minutes in Development) - allow about an hour for the project.
- The reconciliation tests need a second operator: `platform.admin` (signs in with `EWP_PASSWORD_DEFAULT` unless `.env` names its own password).
- `30-journeys/partner-products` (Digital Gold) needs the Partner Bank (`Start-PartnerBank.ps1 -Lan` on the PC, hosts entries for `*.ewp.home.arpa`), Temporal (`ps\temporal\Start-Temporal.ps1`) and the Partner Products Worker. Without the Partner Bank the file is skipped, not failed. Each run onboards a NEW customer: the Partner Bank opens one account per customer and keeps it across EWP database re-creates. The payment-refused test switches the payment network simulator to Refusing and back.
- `40-security/api/token-parity` asks the IDP for a client-credentials token of the KYC case-opening subscriber (a token for ANOTHER API); its development-only secret is read from that subscriber's `appsettings.Development.json` at run time.

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

### Running

The APIs are usually running while you work, and they lock their own `bin` folders - so build the tests somewhere else:

```powershell
dotnet test tst\dotnet\EwpTests.slnx --artifacts-path $env:TEMP\ewp-tests
```

In Visual Studio: open `EwpTests.slnx` in a second instance and use Test Explorer (stop the Payments and Accounts APIs first, or the build cannot overwrite their DLLs).

### Conventions

- Plain NUnit in Given / When / Then style: method names `Given_<situation>_When_<action>_Then_<outcome>`, and `// Given`, `// When`, `// Then` sections.
- `[Category]` uses the Gherkin tags of BSS-Requirements.md: `Happy`, `Boundary`, `Security`, `SoD`, `Resilience`, `Compensation`.
- File and class: `<Class>_<Method>_<Happy|Edge>_<Case>`; several inputs through `[TestCase]`.
- Decisions, not coverage: aggregates, value objects, policies, authorization handlers and the decision branches of command handlers. `Program.cs`, DTOs, EF mappings, thin controllers and hosted-service plumbing are not unit-tested.
- Test data comes from small helpers (`Support\*TestData.cs`) that build aggregates through their own public factories and methods - the domain has no public setters, so NBuilder is not used.