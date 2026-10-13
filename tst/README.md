# EWP V3 tests

| Folder | What | Tooling |
|---|---|---|
| [playwright](playwright) | End-to-end, journey and security tests through the real front ends, in Microsoft Edge | Playwright (TypeScript), run from VS Code |
| `dotnet` (to come) | Unit tests of the .NET libraries and APIs; adapter tests against simulated back ends | NUnit, NBuilder, Moq, AwesomeAssertions, WireMock.Net - own solution, not the main one |

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
    35-resilience\     the journeys while a simulated external system refuses, is down or is slow
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

### Conventions

- Arrange / Act / Assert sections in every test; multi-step walk-throughs use `test.step` so each step is named in the report.
- No fixed sleeps: anything that happens through Kafka is awaited with `expect.poll` / `toPass` and a time limit.
- The tests share one platform and its data, so they run one at a time (`workers: 1`). Records created by tests carry unique names; the seed scenarios create the fixed demo customers.
- Failed sign-ins use an unknown username, never a real user's wrong password (that would count towards the user's lockout).