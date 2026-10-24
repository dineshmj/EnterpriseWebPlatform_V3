# Container images

How EWP's services become container images, and what an image needs from its environment to run.
Every image is listed in [.github/container-images.json](../.github/container-images.json): the 8
.NET domain APIs, the IDP, the Shell, the 4 .NET BFFs, the Outbox publisher, the 8 Kafka
subscribers, the 3 simulators, and the 8 Node.js services - the KYC BFF, the Audit, Operations
and Partner Products web BFFs and Journey APIs, and the Partner Products Worker (35 images).

In short: **CI builds every image from a commit, checks it, and publishes it only if the checks
pass. An image holds no secret and no environment's settings: each environment supplies settings
and secrets when the container starts, so one image runs everywhere.**

## Decisions

| # | Decision | Why |
|---|---|---|
| 1 | Images are built **only in GitHub Actions** ([container-images.yml](../.github/workflows/container-images.yml)) and published to GHCR (`ghcr.io/<owner>/ewp-<service>`); nothing is built on a developer's machine | An image that runs somewhere must come from a known commit through the same checks. (The development laptop also has no memory to spare for Docker.) |
| 2 | One multi-stage **Dockerfile** per deployable, next to its project; build context = repository root | Services share `src/Common` and `src/AsyncWorkflows`; .NET and Node images follow one reviewable pattern |
| 3 | .NET runtime: `mcr.microsoft.com/dotnet/aspnet:<version>-noble-chiseled-extra` | Chiseled = no shell, no package manager, non-root user `app` (UID 1654). `-extra` adds ICU and time zones (amounts formatted in en-AU). Workers use the ASP.NET runtime too: they serve `/health/*` and `/metrics` from Kestrel |
| 3a | Node runtime: **distroless** `gcr.io/distroless/nodejs24-debian13:nonroot`, pinned by digest - Node and the few Debian libraries it needs, no shell, no package manager (no apt, npm, npx, corepack or yarn); the non-root user `nonroot` (UID 65532); production dependencies only, from the committed lockfile. The build stages use `node:<version>-trixie-slim` (the same Debian 13), so native modules are built against the libraries the runtime has | The Node counterpart of chiseled: nothing can be installed into, or run through a shell in, a running container. Distroless is rebuilt with each Debian fix, so a fix does not wait for the official Node image (perl-base, DLA-4821-1, failed the first Node scan on `bookworm-slim`). Its tags move (`nonroot`, no exact Node version), which the digest pin makes safe: the Node patch level is the one in the pinned digest. The Debian 12 distroless Node 24 image has not been updated since February 2026 (its versions are tagged `deprecated-public-image-*`) |
| 4 | Plain **HTTP on 8080** inside the container; TLS at the platform (ingress for browsers, service mesh mTLS between pods) - the same line as Temporal (Threat-Model R-16). The Node services turn it on with `<PREFIX>_PLAIN_HTTP=true` (set in their images); without it they need their development certificate, and both set refuse to start | Certificates and their rotation belong to the platform, not to 35 images; a missing setting must not quietly open plain HTTP |
| 5 | Settings and secrets **only from the environment** at run time (`Section__Key` variables, or files mounted from a secret store) | The same image runs in every environment; a secret in a layer stays in the registry for good |
| 6 | Base images pinned by **tag and digest** (`image:tag@sha256:...`), actions pinned to commit SHAs; **Dependabot** proposes updates weekly - all base image moves in one pull request ([dependabot.yml](../.github/dependabot.yml)) | A re-pushed tag cannot change an image between two builds; nothing changes silently, security fixes still arrive, and all 35 images stay on the same bases |

## What CI does per image

0. **Which images** (once per run): an image is built when one of the folders listed for it in
   the manifest changed - its project and every shared project it references. A change to the
   build itself (the workflow, the manifest, `.dockerignore`, `Directory.Build.props`) or a manual
   run builds all of them. The run's summary lists the images picked.
1. **hadolint**: lints the Dockerfile (warnings fail).
2. **Build** with Buildx (layer cache in GitHub Actions).
3. **Trivy**: scans the built image for vulnerabilities that have a fix and for secrets. HIGH or
   CRITICAL fails the job, and nothing is published. The report goes to the run's summary page
   (passed or failed), so it can be read without opening the job log.
4. **Smoke check** (images with a `smoke` entry in the manifest - today the Partner Products
   Worker): loads the native modules inside the built image, without network and with a
   read-only file system, so a library the distroless runtime lacks fails the job.
5. On `main` only: **push** to GHCR with an **SBOM** and BuildKit's **SLSA provenance** attached.
   Tags are the full commit SHA (a deployment pins this one) and the branch name.

Pull requests run steps 1-4 and never publish.

## Registry retention

[ghcr-retention.yml](../.github/workflows/ghcr-retention.yml) runs every Monday (and by hand) and
keeps, per image, the newest **10** versions with a commit tag plus the one tagged `main`
([ghcr-retention.sh](../.github/scripts/ghcr-retention.sh)). An older version is deleted together
with the manifests its index lists - the platform image and its SBOM / provenance attestation,
which GHCR shows as **untagged** versions - except a manifest that a kept image lists too (a build
from the cache can reuse one). An untagged version is never deleted on its own. A manual run is a
dry run unless `dry_run` is switched off, and `keep` can be raised (at least 3). The run's summary
lists, per image, how many were kept and deleted. A deleted version can be restored for 30 days
from the package's settings. A deployment that still pins an older commit SHA must move forward
before its image ages out.

## Keeping secrets out of images

- [.dockerignore](../.dockerignore) keeps every `appsettings.Development.json`, `.env*`, every
  `runnow.bat` (they set the Node services' development secrets), and all key material (`*.pem`,
  `*.pfx`, `*.crt`, `*.jwk`, `keys/`, `certs/`, ...) out of the build context. A `COPY` of a whole
  project folder therefore cannot carry them into an image.
- The Node services read every setting from environment variables already (`runnow.bat` sets the
  development values); none has a default for a secret or an address.
- Connection strings carry passwords, so they move from `appsettings.json` (which goes into the
  image) to `appsettings.Development.json`. **Done for every .NET service** (the domain
  APIs, the IDP, the Shell, the 4 BFFs, the Outbox publisher). A host without a connection string
  refuses to start.
- Trivy's secret scanner checks the finished image as a second line of defence.

## Run-time contract

Every .NET image runs as UID 1654, listens on 8080, and has `ASPNETCORE_ENVIRONMENT` unset
(Production); every Node image runs as UID 65532 with `NODE_ENV=production`. Their files belong to
root, so the root file system can be read-only; give it a writable `/tmp` (emptyDir). Probes:
`/health/live` and `/health/ready` on 8080 (.NET workers: their `Health__Urls` is set to
`http://+:8080` in the image; the Partner Products Worker: `PARTNER_PRODUCTS_WORKER_HEALTH_PORT`).
The Next.js BFFs answer both probes in `server.mjs` once the app is prepared; readiness also asks
their session database (`SELECT 1`, 2 s, its own one-connection pool), like the KYC BFF. (They use no Next.js feature that writes a cache at run time - ISR, image
optimisation; one that did would need an emptyDir at `.next/cache`.)

**What every image needs:** the settings in its `appsettings.json` that hold local addresses
(IDP, other APIs, Kafka bootstrap servers), overridden per environment; and the secrets that its
`appsettings.Development.json` lists under `//Secrets` / `//ConnectionStrings` - always from the
environment or a secret store. Kafka clients also need the Kafka trust root as a mounted file
(`Kafka__SslCaLocation`). A Node image needs the variables its `runnow.bat` sets (the development
values are there), except `<PREFIX>_PORT`, `<PREFIX>_PLAIN_HTTP` and `<PREFIX>_TLS_PFX_*`, which the
image sets or does not use.

**Beyond that:**

| Image | Needs |
|---|---|
| `ewp-idp` | The token-signing certificate (PFX) mounted from the secret store: `SigningCredential__CertificatePath`, `SigningCredential__CertificatePassword` (no developer key outside Development - it refuses to start); each private_key_jwt client's PUBLIC key file: `ClientKeys__<client id>` (e.g. `ClientKeys__SelfService.Web.ClientID`); every confidential client's secret: `ClientSecrets__<client id>`; `ClientBaseUrls__*` (the web clients' public https addresses, for their redirect URIs). The issuer is the address the browser uses (`https` from the ingress, see below) |
| `ewp-shell` | `MfeOrigins__*` (the MFEs the Shell may frame and its menu links to, by key) and `NotificationsApiBaseUrl`, all https. The menu database names each MFE by key only (`microservices.mfe_key`), so the same menu data serves every environment; a key without an `MfeOrigins` entry is left out of the menu |
| `ewp-audit-api` | A write-once volume for the signed checkpoints: `Audit__Anchors__Directory`; the anchor signing key from the secret store |
| `ewp-documents-management-api` | A volume for the encrypted documents: `DocumentStorage__RootPath`; the key ring (`DocumentStorage__Encryption__*`) from the secret store |
| `ewp-partner-products-api` | EWP's mTLS client and request-signing certificates and their password files, mounted from the secret store (`PartnerBank__*CertificatePath`, `*PasswordPath`) |
| `ewp-customer-kyc-bff-web` | `KYC_BFF_TRUSTED_PROXIES` - the ingress's addresses; without it the BFF refuses to start on plain HTTP (see below). Secrets: `KYC_BFF_CLIENT_SECRET`, `KYC_BFF_SESSION_SECRET`, `KYC_BFF_SESSION_KEY`, `KYC_BFF_DATABASE_URL` |
| `ewp-audit-bff-web`, `ewp-operations-bff-web`, `ewp-partner-products-bff-web` | `<PREFIX>_TRUSTED_PROXIES` (`AUDIT_WEB_`, `OPERATIONS_WEB_`, `PARTNER_PRODUCTS_WEB_`) for the client address; `<PREFIX>_PUBLIC_ORIGIN` - the https address the browser uses (redirect URIs). Secrets: `<PREFIX>_CLIENT_SECRET`, `<PREFIX>_SESSION_KEY`, `<PREFIX>_DATABASE_URL` |
| `ewp-partner-products-worker` | Temporal (`TEMPORAL_ADDRESS`, `TEMPORAL_NAMESPACE`); the Kafka trust root as a mounted file -> `KAFKA_SSL_CA_LOCATION`. Secrets: `PARTNER_PRODUCTS_WORKER_CLIENT_SECRET`, `PARTNER_PRODUCTS_WORKER_KAFKA_PASSWORD` |
| `ewp-operations-journey-api` | `OPERATIONS_JOURNEY_PLATFORM_FILE`: a mounted file (ConfigMap) listing every component's readiness address and the simulators' addresses - the cluster's own; the development one is `config/platform.development.json`, which is not in the image |
| `ewp-*-simulator` | Development, test and demo environments only - never deployed to production |

The Payments images in full, as the worked example:

| Image | Probes (8080) | Required from the environment | Mounted files |
|---|---|---|---|
| `ewp-payments-api` | `/health/live`, `/health/ready` | `ConnectionStrings__PaymentsDbConnection`, `Kafka__BootstrapServers`, `Kafka__SaslPassword`, `Authentication__Authority`, `PaymentNetwork__BaseUrl` (https), `PaymentNetwork__PermittedHosts__0`, `PaymentNetwork__ApiKey`, `AllowedHosts` | Kafka trust root -> `Kafka__SslCaLocation` |
| `ewp-payments-bff-web` | `/health/live`, `/health/ready` | `ConnectionStrings__BffStateDbConnection`, `Oidc__Authority`, `Oidc__ClientSecret`, `ShellOrigin`, `PaymentsBff__PaymentsApiBaseUrl`, `PaymentsBff__AccountsApiBaseUrl` (both https), `AllowedHosts`, `ForwardedHeaders__KnownNetworks__0` (see below) | - |
| `ewp-payments-saga-reply-subscriber` | `/health/live`, `/health/ready` (`Health__Urls` is set to `http://+:8080` in the image) | `Kafka__BootstrapServers`, `Kafka__SaslPassword`, `PaymentsSagaReplySubscriber__IdentityServerAuthority`, `PaymentsSagaReplySubscriber__ClientSecret`, `PaymentsSagaReplySubscriber__PaymentsApiBaseUrl` | Kafka trust root -> `Kafka__SslCaLocation` |

`/metrics` is off outside Development. A deployment turns it on with
`Observability__Metrics__Enabled=true`, and only where the scraper alone can reach it.

The IDP's and the APIs' certificates must be trusted by the containers: either issued by a public
CA, or the private root added to the image's trust store by the platform. Node does not read the
operating system's trust store: give the Node images a private root as a mounted file in
`NODE_EXTRA_CA_CERTS`.

### Behind the ingress

The ingress ends TLS, so the IDP, the Shell and the .NET BFFs must learn the original scheme and
client address from `X-Forwarded-Proto` / `X-Forwarded-For` (otherwise the IDP publishes `http://`
endpoints and a BFF sends `http://` sign-in redirect URIs, which the IDP refuses). They trust those
headers **only from the ingress**: list its addresses as `ForwardedHeaders__KnownNetworks__0`
(CIDR, e.g. the ingress subnet) or `ForwardedHeaders__KnownProxies__0` (IP). Nothing listed = no
forwarded header is trusted (local runs). The Host header is never taken from a forwarded header;
`AllowedHosts` checks the one the ingress passes on
([ProxyForwardedHeaders.cs](../src/Common/WebUtilities/Security/ProxyForwardedHeaders.cs)).

The Node BFFs follow the same rule with `<PREFIX>_TRUSTED_PROXIES` (IPs or CIDRs, comma-separated,
one hop; nothing listed = no forwarded header is trusted):

- **KYC BFF**: express's `trust proxy` is limited to those addresses
  ([trusted-proxies.ts](../src/Microservices/CustomerKyc/BFF.Web/src/security/trusted-proxies.ts)).
  It is **required** with plain HTTP: only the ingress's `X-Forwarded-Proto` makes express see
  HTTPS, and express-session sets the `Secure` session cookie only then. `X-Forwarded-Host` is
  always dropped.
- **Next.js BFFs**: `server.mjs` keeps `X-Forwarded-For` / `-Proto` only from those addresses and
  drops `X-Forwarded-Host` / `-Port` always
  ([forwarded-headers.mjs](../src/Microservices/Audit/BFF.Web/forwarded-headers.mjs)). They build
  their redirect URIs from `<PREFIX>_PUBLIC_ORIGIN`, not from the request, so the list is
  optional - but the ingress must pass the original Host header on: Next.js checks a server
  action's `Origin` against it.

### When PostgreSQL is unreachable

Sessions, key rings and every domain record are in PostgreSQL, so while it is unreachable nothing
signed-in can be served - by design. What the platform guarantees is a quick, plain answer:

- Every connection gives up after **5 s** (Npgsql `Timeout`, `pg` `connectionTimeoutMillis`); a
  connection string that sets its own `Timeout` keeps it.
- Every web host (IDP, Shell, .NET BFFs and APIs: [DatabaseOutage.cs](../src/Common/WebUtilities/Resilience/DatabaseOutage.cs);
  the Next.js BFFs: `lib/server/outage.ts`; the KYC BFF: `src/resilience/database-outage.ts`)
  answers **503 + Retry-After: 15** - a page that reloads itself for a page load (also inside the
  Shell's frame), Problem Details for a call. The Next.js BFFs' server actions return
  "temporarily unavailable" to the screen. The Shell shows "temporarily unavailable" and asks again
  every 15 s instead of sending the person to sign in.
- Readiness reports the database (`/health/ready`), so with several instances the ingress stops
  routing to an instance that cannot reach it.

Production keeps the outage short with a highly available PostgreSQL (e.g. zone-redundant HA with
automatic failover); the answers above cover the minutes until then.

### The micro front ends' Shell origin

The Shell origin is the only window an MFE exchanges messages with. It is **not** compiled into
the front end: each BFF serves `/ewp-runtime-config.js` from its own `ShellOrigin` setting
(.NET: [MfeRuntimeConfig.cs](../src/Common/WebUtilities/Security/MfeRuntimeConfig.cs); KYC:
`main.ts`; Next.js BFFs: `app/ewp-runtime-config.js/route.ts`). The layout loads it as a
`beforeInteractive` script, so Next.js has it before it starts the page, and each MFE's
`shell-origin.ts` reads it. Missing, or not an https origin = the MFE sends and accepts no message
(fail closed, with a console error).

## Known gaps (to close at the deployment step)

| Gap | Effect | Planned fix |
|---|---|---|
| The BFF validates that API addresses are `https`, but the API containers listen on HTTP | Pod-to-pod calls need TLS from the platform | Choose at deployment: mesh mTLS with https service addresses, or Kestrel TLS from a mounted certificate (`ASPNETCORE_URLS=https://+:8443`, `ASPNETCORE_Kestrel__Certificates__Default__Path`); no code change either way |
| The Dockerfile front end (`# syntax=docker/dockerfile:1.7`) is pinned by tag only | A re-pushed front-end tag could change how a Dockerfile is read | Pin it by digest too, once Dependabot (or a weekly check) can move that pin |
| The distroless runtime's Node patch level is set by its digest, the build stages' by their tag (`24.22.1`) | Build and runtime can be one Node patch apart for a week | Accepted: same major and Debian release; the smoke check loads the native modules in the runtime |
| The Partner Products Worker's Kafka client uses a prebuilt librdkafka binding fetched at install; CI loads it (and Temporal's native core) in the image, but the image has not yet connected to Kafka or Temporal | A TLS or SASL problem inside the container would show only at start-up | First deployment: start the worker image against Kafka and Temporal |
| The Notifications API pushes over SignalR without a backplane | With several instances, a push reaches only the browsers connected to the instance that received the event | Run ONE replica until a backplane (Redis, or Azure SignalR Service) is added; every other service may run several (the IDP's replay cache, the BFF sessions and Data Protection keys, the Outbox relays and workers are shared or locked in PostgreSQL) |

## Adding an image

1. Copy the closest Dockerfile (.NET: API / BFF with client-app / worker; Node: Next.js BFF -
   Audit web, NestJS service - Audit Journey API, NestJS BFF with a static export - KYC BFF).
   Change the project paths and the `COPY` lines of the shared projects it references
   (`ProjectReference`; for Node, `src/Common/DesignSystem` when its CSS imports the tokens).
   Keep the `FROM` lines exactly as copied (tag and digest), so every image has the same bases.
2. Move any connection string with a password from `appsettings.json` to
   `appsettings.Development.json`, and record it in this page's run-time contract. A Node service
   takes `<PREFIX>_PLAIN_HTTP` (copy `listener-tls.ts`) and, if browsers reach it,
   `<PREFIX>_TRUSTED_PROXIES`.
3. Add the image to [.github/container-images.json](../.github/container-images.json) - its name,
   Dockerfile and EVERY folder it is built from (its project, each shared project it references,
   anything else its Dockerfile copies); a missing folder means a change there does not rebuild
   the image. A Node image with native modules also gets a `smoke` entry (a `node -e` snippet that
   requires them). Add its folder to [dependabot.yml](../.github/dependabot.yml).
4. Add a "Container image" row to the project README's "Security controls" table.