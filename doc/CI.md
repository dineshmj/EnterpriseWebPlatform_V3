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
| Node (one job per project) | Every `src/**/package.json`, found automatically: install from the committed lockfile (`--frozen-lockfile`) -> Next.js route types (`next typegen`) -> type check -> `pnpm audit --prod` -> tests where a `test` script exists (the Partner Products Journey API: Jest against the real WireMock jar, pinned to 3.13.2 and checked against Maven Central's SHA-256) | A lockfile out of step with `package.json`; a type error; a HIGH or CRITICAL advisory in a production dependency; a failing test |
| Helm charts | [check-charts.sh](../deploy/helm/check-charts.sh): `helm lint --strict` + `helm template` of `deploy/helm/ewp-cluster`, `deploy/helm/ewp` with every `values-*.yaml` and `deploy/helm/ewp-gateway` -> kubeconform (strict) against the Kubernetes 1.34 schemas and the cert-manager / trust-manager / Gateway API / Envoy Gateway schemas (CRDs catalog at a pinned commit) -> each chart must refuse to render without each required value -> the gateway's public certificate must list exactly the EWP chart's public hosts. Helm 4.3.1 and kubeconform 0.8.0, each kept only with its published SHA-256 ([Kubernetes.md](Kubernetes.md)) | A lint error; an object that does not match its schema (strict: unknown fields too); a required value no longer refused; a public host missing from the certificate |
| Terraform | The cloud demo's code ([deploy/terraform/demo](../deploy/terraform/demo)): `terraform fmt -check` -> `terraform init -backend=false -lockfile=readonly` (no Azure, no credentials; providers only with the checksums in `.terraform.lock.hcl`) -> `terraform validate`; `bash -n` of `.github/scripts/demo-*.sh`. Terraform 1.16.7 from [demo-tools.sh](../.github/scripts/demo-tools.sh), kept only with its published SHA-256 ([Cloud-Demo.md](Cloud-Demo.md)) | Unformatted code; a provider whose checksum differs from the lock file; an invalid configuration; a shell syntax error |
| **CI result** | Sums up the jobs above | Any of them did not succeed. **This is the one check branch protection requires** |

Not gated here, and why:

- **npm build tooling (dev dependencies)**: not deployed; `ps/build/Scan-Dependencies.ps1`
  reports it as warnings locally.
- **Container images**: [container-images.yml](../.github/workflows/container-images.yml) builds
  and scans the images a pull request changes, but it has a path filter, so it cannot be a
  required check. Its scan still stops a vulnerable image from being published on `main`.
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

Dependabot's pull requests (actions, base images) go through the same gates.