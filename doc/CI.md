# CI gates

What every change must pass before it reaches `main`, and how `main` is protected.

In short: **nothing reaches `main` without a pull request whose "CI result" check is green - the
solution builds, the unit tests pass, and no deployed dependency has a known HIGH or CRITICAL
vulnerability.** Images are then built from `main` only ([Containers.md](Containers.md)).

## The gates

[ci.yml](../.github/workflows/ci.yml) runs on every pull request and every push to `main` (no
path filter: a required check must always report).

| Job | What it checks | Fails when |
|---|---|---|
| .NET | Restore -> vulnerable packages (`dotnet list package --vulnerable --include-transitive`, the solution's deployed packages) -> Release build of `EnterpriseWebPlatform.BSS.slnx` -> unit tests (`tst/dotnet/EwpTests.slnx`) | A HIGH or CRITICAL advisory; a build error; a failing test. Lower advisories are listed on the run's summary |
| Node (one job per project) | Every `src/**/package.json`, found automatically: install from the committed lockfile (`--frozen-lockfile`) -> Next.js route types (`next typegen`) -> type check -> `pnpm audit --prod` -> tests where a `test` script exists (the Partner Products, Audit and Operations Journey APIs: Jest against the real WireMock jar, pinned to 3.13.2 and checked against Maven Central's SHA-256) | A lockfile out of step with `package.json`; a type error; a HIGH or CRITICAL advisory in a production dependency; a failing test |
| Helm charts | [check-charts.sh](../deploy/helm/check-charts.sh): `helm lint --strict` + `helm template` of `deploy/helm/ewp-cluster`, `deploy/helm/ewp` with every `values-*.yaml`, `deploy/helm/ewp-gateway`, `deploy/helm/ewp-infra` and `deploy/helm/keyvault-secrets` -> kubeconform (strict) against the Kubernetes 1.34 schemas and the cert-manager / trust-manager / Gateway API / Envoy Gateway / Strimzi / Secrets Store CSI schemas (CRDs catalog at a pinned commit) -> each chart must refuse to render without each required value -> the gateway's public certificate must list exactly the EWP chart's public hosts -> the cloud demo's contracts ([check-contracts.mjs](../deploy/helm/check-contracts.mjs): every Secret key a chart reads is made by [demo-secrets.mjs](../.github/scripts/demo-secrets.mjs) and the other way round; database roles, Kafka users and topics as on the laptop) -> the demo's throw-away PKI (`demo-secrets.mjs --check-pki`). Helm 4.3.1 and kubeconform 0.8.0, each kept only with its published SHA-256 ([Kubernetes.md](Kubernetes.md)) | A lint error; an object that does not match its schema (strict: unknown fields too); a required value no longer refused; a public host missing from the certificate; a secret, role, Kafka user or topic out of step; a demo certificate the services would refuse |
| Terraform | The cloud demo's code ([deploy/terraform/demo](../deploy/terraform/demo)): `terraform fmt -check` -> `terraform init -backend=false -lockfile=readonly` (no Azure, no credentials; providers only with the checksums in `.terraform.lock.hcl`) -> `terraform validate`; `bash -n` of `.github/scripts/demo-*.sh`. Terraform 1.16.7 from [demo-tools.sh](../.github/scripts/demo-tools.sh), kept only with its published SHA-256 ([Cloud-Demo.md](Cloud-Demo.md)) | Unformatted code; a provider whose checksum differs from the lock file; an invalid configuration; a shell syntax error |
| **CI result** | Sums up the jobs above | Any of them did not succeed. **This is the one check branch protection requires** |

Not gated here, and why:

- **npm build tooling (dev dependencies)**: not deployed; `ps/build/Scan-Dependencies.ps1`
  reports it as warnings locally.
- **Container images**: [container-images.yml](../.github/workflows/container-images.yml) builds
  and scans the images a pull request changes, but it has a path filter, so it cannot be a
  required check. Its scan still stops a vulnerable image from being published on `main`.
- **Code scanning (CodeQL, SonarQube Cloud, Snyk)**: reported, not gating yet - see
  [Code scanning](#code-scanning-reported-not-gating) below.
- **Playwright**: needs the whole platform running (laptop, or the cloud demo later).
- **Compiler warnings**: reported, not failing (3 nullable warnings in the IDP today).
- **Azure access check**: [azure-access-check.yml](../.github/workflows/azure-access-check.yml)
  is started by hand from `main`, needs the `azure-demo` environment's approval, and checks the
  demo identity's rights in Azure ([Azure.md](Azure.md)). It is not a pull request gate.
- **Demo up / Demo down / Demo watchdog**: the cloud demo's workflows run against Azure from
  `main` only ([Cloud-Demo.md](Cloud-Demo.md)); CI checks their Terraform and scripts without
  Azure (the Terraform job above).

A new advisory can turn a gate red without a code change (as body-parser and multer did on
24-OCT). That is intended: fix the dependency (an update, or an override in
`pnpm-workspace.yaml` that rewrites only the vulnerable versions) in the next pull request.

## Code scanning (reported, not gating)

[code-scanning.yml](../.github/workflows/code-scanning.yml) runs on every pull request, every
push to `main`, weekly (Monday 04:17 UTC: new queries and new advisories find issues in unchanged
code) and by hand. **None of its jobs is part of "CI result": a finding never blocks a merge.**
That is deliberate for a first run - the existing code has never been scanned, and a gate set
before the findings are triaged would only be switched off again. Once they are, a gate can be
added (for example: no new HIGH CodeQL alert on a pull request).

| Job | What it does | Where the findings go |
|---|---|---|
| CodeQL (C#, JavaScript/TypeScript, Actions) | GitHub's data-flow analysis, `security-extended` queries, `build-mode: none` (read from source, no build). The workflows are scanned too (script injection, over-broad tokens) | **Security -> Code scanning**; new alerts on a pull request are annotated on its diff |
| SonarQube Cloud | Scanner for .NET 12.0.0 around the Release build of `EnterpriseWebPlatform.BSS.slnx`; it also analyses the TypeScript outside the .NET projects, so one project shows C# and TypeScript (bugs, code smells, security hotspots, duplication) | sonarcloud.io; its quality gate shows on the pull request as a separate check |
| Snyk Open Source | Snyk CLI 1.1308.1 (pinned, Snyk's SHA-256 checked): `snyk test --all-projects` over the restored NuGet projects and the pnpm lockfiles, test projects left out (as in the CI gate), HIGH and above. Snyk writes one SARIF run per project (47 today) and code scanning takes at most 20 per upload, so they are merged into one run first. On `main` (push, schedule or by hand - not pull requests) also `snyk monitor`, so Snyk alerts on a NEW advisory against what `main` ships, without a commit | **Security -> Code scanning** (category `snyk-open-source`) and app.snyk.io |

SonarQube Cloud and Snyk skip themselves (and say so on the run's summary) until their token
exists. Fork and Dependabot pull requests get no secrets, so they skip there too.

Snyk and `pnpm audit` can rate the same advisory differently: `qs` 6.16.0 (GHSA-6pq8-jhm2-2vcw,
28-OCT-2026) was Medium for GitHub, so the CI gate passed, and High for Snyk. It was fixed with
an override like the others.

**The tokens.** `SONAR_TOKEN` is a *personal* SonarQube Cloud token with **no expiry**: scoped
organisation tokens need the paid Team plan, and an expiring token would turn the job red on its
own one day. Accepted for this PoC because the token can only act in one free organisation with
one public project, and GitHub hands it only to workflows of this repository (not to forks). If
it may have leaked: revoke it in SonarQube Cloud (**My account -> Access tokens**), generate a
new one, replace the secret. `SNYK_TOKEN` is the account's auth token; the same applies (Snyk:
**Account settings -> Auth Token -> Regenerate**). Snyk's GitHub integration (repository import,
fix pull requests) is deliberately NOT connected: it would ask for write access to every public
repository and duplicate Dependabot.

**Triage, 28-OCT-2026 (first full scan).** Fixed: a committed private key (the KYC BFF's copy of
the development certificate - now taken from `%USERPROFILE%\.aspnet\https` like every Node
service, and `*.pfx` / `*.key` / `certs/` are git-ignored), regular expressions on input without
a match timeout, a locale-dependent sort, a file check outside the KYC BFF's static root,
`curl` downloads that could follow a redirect to plain HTTP, `qs` 6.16.0. Dismissed as false
positives, with the reason on each alert: CSRF on the Domain APIs (bearer tokens only - no
cookie, so no cross-site request can carry credentials), reflected XSS in the Journey APIs'
Problem Details (sent as `application/problem+json` with `nosniff`, never HTML), log forging (the
.NET services log JSON everywhere but Development; Development's text console escapes control
characters in every value since 29-OCT-2026 - before that, a typed reason with a line break could
add a fake entry there; the Journey APIs log `req.path`, which Express does not URL-decode and
Node's HTTP parser refuses with a raw line break), SQL injection in the Shell's
`MenuRepository` (only `@p0..` placeholders are formatted in; the values are parameters).
`db/` is excluded from SonarQube Cloud: it reads T-SQL as Oracle PL/SQL.

**SonarQube Cloud clean-up, 29-OCT-2026.** Before: 51 vulnerabilities, 45 bugs, 213 code smells,
14.6 % duplication. Fixed in the code: install scripts off in every image and CI install
(`--ignore-scripts`, see [Containers.md](Containers.md) 3b); the Payments BFF forwards decisions
with constant path segments; `[JsonRequired]` on the values a request must carry (under-posting);
`crypto.randomInt` for the BFFs' session clean-up; cookies deleted with `HttpOnly`; request
cancellation passed to response writes; `await app.RunAsync()`; namespaces for the simulators'
types and the IDP's `Config`; fixed log templates; backtracking-free regular expressions; `[[ ]]`
in the bash scripts; `autofocus` removed from the IDP's pages. Marked `NOSONAR` with the reason on
the line (deliberate, and reviewed): the upload size limits for identity documents, the Shell's
menu query (placeholders only), tools found on `PATH` in CI and test scripts, Key Vault purge
protection off in the demo (Demo down deletes everything), `http://` in the gateway's redirect
check and in the OIDC back-channel-logout event name, `ToUpper()` inside an EF query (it becomes
SQL `UPPER`), the launcher's own `Clear-Host`. Turned off: CA1873 (logging arguments are plain
values - see [.editorconfig](../.editorconfig)). Excluded: `**/*.sql` (SonarQube Cloud reads T-SQL and
PostgreSQL as Oracle PL/SQL). **Duplication stays by design**: each micro front end and its BFF is
its squad's own deployable, so their start-up code is not shared through a central project.
The look-alike scaffold files are therefore left out of SonarQube Cloud's duplication count
(`sonar.cpd.exclusions` in [code-scanning.yml](../.github/workflows/code-scanning.yml)):
`Program.cs`, `AuthController.cs`, `*DbContext.cs`, `*OutboxPublisher.cs`, `MfeShell.tsx`,
`components/ui/feedback.tsx`, `lib/server/oidc.ts`, `lib/server/session-store.ts`. All other rules
still run on them, and copy-paste anywhere else still counts. A comment cannot do this: `NOSONAR`
and `#pragma` silence issues, not duplication. Start-up stays inline in each `Program.cs` (no
`builder.AddXxx()` helpers) so a reader sees the whole pipeline in one file.

**SonarQube Cloud's quality gate is red on every pull request - by decision.** The free plan
allows only the built-in "Sonar way" gate, which wants 80 % test coverage on new code, and no
coverage is sent to SonarQube Cloud, so it reads 0 %. The check is not required (the branch rule
asks only for **CI result**), so it never blocks a merge; bugs, vulnerabilities and hotspots are
still reported on each pull request. To make it green: send coverage (coverlet OpenCover from the
.NET tests, lcov from Jest) and exclude what has no unit tests (`Program.cs`, the simulators, the
Next.js BFFs) - a step of its own.

### Setting up SonarQube Cloud (once)

1. [sonarcloud.io](https://sonarcloud.io) -> **Log in with GitHub** -> import the GitHub
   organisation `dineshmj` (free plan; public projects are free) -> **Analyze new project** ->
   `EnterpriseWebPlatform_V3`.
2. In the project: **Administration -> Analysis Method** -> **Automatic Analysis off** (the
   workflow does the analysis; both together fail).
3. **My Account -> Security** -> generate a token -> GitHub repository **Settings -> Secrets and
   variables -> Actions -> New repository secret** `SONAR_TOKEN`.
4. Only if the organisation key or project key SonarQube Cloud shows differ from `dineshmj` and
   `dineshmj_EnterpriseWebPlatform_V3`: add them as repository **variables** `SONAR_ORGANIZATION`
   and `SONAR_PROJECT_KEY`.

### Setting up Snyk (once)

1. [app.snyk.io](https://app.snyk.io) -> sign up with GitHub (free plan).
2. **Account settings -> Auth Token** (or a service account token in the organisation settings)
   -> repository secret `SNYK_TOKEN`.
3. Do not also import the repository through Snyk's GitHub integration: it would open its own
   fix pull requests next to Dependabot's.

Then **Actions -> Code scanning -> Run workflow** on `main` once, to see the first results.

## Branch protection: ruleset for `main`

Set once in GitHub: **Settings -> Rules -> Rulesets -> New ruleset -> New branch ruleset**.

| Setting | Value |
|---|---|
| Ruleset name | `main` |
| Enforcement status | Active |
| Bypass list | Empty (the rules apply to the owner too) |
| Target branches | Add target -> Include default branch |
| Restrict deletions | On |
| Block force pushes | On |
| Require a pull request before merging | On; required approvals **0** (one developer); allowed merge methods: **Squash** |
| Require status checks to pass | On; add **`CI result`** (source: GitHub Actions); "Require branches to be up to date" off |

`CI result` appears in the list only after the workflow has run once - add the ruleset after the
first green run of `ci.yml`.

Also once: **Settings -> Advanced Security** (free for a public repository) -> **Secret
Protection** on, and **Push protection** on. GitHub then refuses a push that contains a known
secret format (cloud keys, tokens) - before it reaches the repository, where it would stay in the
history.

## Working with it

From the laptop or the PC:

```text
git switch main && git pull
git switch -c <step-name>             # e.g. ci-gates, dpop-replay-cache
... commit as usual ...
git push -u origin <step-name>
```

On GitHub: **Compare & pull request**. The PR description is the commit description; with
squash merge it becomes the one commit on `main`. When "CI result" is green: **Squash and merge**,
then delete the branch. Then locally: `git switch main && git pull`.

Dependabot's pull requests (actions, Terraform providers, base images, NuGet packages - each
weekly, grouped) go through the same gates. A .NET servicing release arrives as two of them, the
packages and the runtime image: merge them together. Why NuGet is there too: the vulnerable-packages
gate sees only package advisories, not fixes in the .NET runtime itself - the 10.0.14 security
release was found by hand, two days late (27-OCT-2026).