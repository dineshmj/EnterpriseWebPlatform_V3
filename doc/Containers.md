# Container images

How EWP's services become container images, and what an image needs from its environment to run.
First slice: the **Payments** context (API, BFF with its micro front end, saga reply subscriber).
The other deployables follow the same pattern (see [Adding an image](#adding-an-image)).

In short: **CI builds every image from a commit, checks it, and publishes it only if the checks
pass. An image holds no secret and no environment's settings: each environment supplies settings
and secrets when the container starts, so one image runs everywhere.**

## Decisions

| # | Decision | Why |
|---|---|---|
| 1 | Images are built **only in GitHub Actions** ([container-images.yml](../.github/workflows/container-images.yml)) and published to GHCR (`ghcr.io/<owner>/ewp-<service>`); nothing is built on a developer's machine | An image that runs somewhere must come from a known commit through the same checks. (The development laptop also has no memory to spare for Docker.) |
| 2 | One multi-stage **Dockerfile** per deployable, next to its project; build context = repository root | Services share `src/Common` and `src/AsyncWorkflows`; .NET and Node images follow one reviewable pattern |
| 3 | .NET runtime: `mcr.microsoft.com/dotnet/aspnet:<version>-noble-chiseled-extra` | Chiseled = no shell, no package manager, non-root user `app` (UID 1654). `-extra` adds ICU and time zones (amounts formatted in en-AU). Workers use the ASP.NET runtime too: they serve `/health/*` and `/metrics` from Kestrel |
| 4 | Plain **HTTP on 8080** inside the container; TLS at the platform (ingress for browsers, service mesh mTLS between pods) - the same line as Temporal (Threat-Model R-16) | Certificates and their rotation belong to the platform, not to 30 images |
| 5 | Settings and secrets **only from the environment** at run time (`Section__Key` variables, or files mounted from a secret store) | The same image runs in every environment; a secret in a layer stays in the registry for good |
| 6 | Exact base image tags and actions pinned to commit SHAs; **Dependabot** proposes updates weekly ([dependabot.yml](../.github/dependabot.yml)) | Nothing changes silently, and security fixes still arrive |

## What CI does per image

1. **hadolint**: lints the Dockerfile (warnings fail).
2. **Build** with Buildx (layer cache in GitHub Actions).
3. **Trivy**: scans the built image for vulnerabilities that have a fix and for secrets. HIGH or
   CRITICAL fails the job, and nothing is published.
4. On `main` only: **push** to GHCR with an **SBOM** and BuildKit's **SLSA provenance** attached.
   Tags are the full commit SHA (a deployment pins this one) and the branch name.

Pull requests run steps 1-3 and never publish.

## Keeping secrets out of images

- [.dockerignore](../.dockerignore) keeps every `appsettings.Development.json`, `.env*`, and all key
  material (`*.pem`, `*.pfx`, `*.jwk`, `keys/`, ...) out of the build context. A `COPY` of a whole
  project folder therefore cannot carry them into an image.
- Connection strings carry passwords, so they move from `appsettings.json` (which goes into the
  image) to `appsettings.Development.json`. **Done for Payments** (API and BFF); the other services
  follow with their images. A host without a connection string refuses to start.
- Trivy's secret scanner checks the finished image as a second line of defence.

## Run-time contract (Payments)

Every image runs as UID 1654, listens on 8080, and has `ASPNETCORE_ENVIRONMENT` unset
(Production). Its files belong to root, so the root file system can be read-only; give it a
writable `/tmp` (emptyDir).

| Image | Probes (8080) | Required from the environment | Mounted files |
|---|---|---|---|
| `ewp-payments-api` | `/health/live`, `/health/ready` | `ConnectionStrings__PaymentsDbConnection`, `Kafka__BootstrapServers`, `Kafka__SaslPassword`, `Authentication__Authority`, `PaymentNetwork__BaseUrl` (https), `PaymentNetwork__PermittedHosts__0`, `PaymentNetwork__ApiKey`, `AllowedHosts` | Kafka trust root -> `Kafka__SslCaLocation` |
| `ewp-payments-bff-web` | `/health/live`, `/health/ready` | `ConnectionStrings__BffStateDbConnection`, `Oidc__Authority`, `Oidc__ClientSecret`, `ShellOrigin`, `PaymentsBff__PaymentsApiBaseUrl`, `PaymentsBff__AccountsApiBaseUrl` (both https), `AllowedHosts`, `ForwardedHeaders__KnownNetworks__0` (see below) | - |
| `ewp-payments-saga-reply-subscriber` | `/health/live`, `/health/ready` (`Health__Urls` is set to `http://+:8080` in the image) | `Kafka__BootstrapServers`, `Kafka__SaslPassword`, `PaymentsSagaReplySubscriber__IdentityServerAuthority`, `PaymentsSagaReplySubscriber__ClientSecret`, `PaymentsSagaReplySubscriber__PaymentsApiBaseUrl` | Kafka trust root -> `Kafka__SslCaLocation` |

`/metrics` is off outside Development. A deployment turns it on with
`Observability__Metrics__Enabled=true`, and only where the scraper alone can reach it.

The IDP's and the APIs' certificates must be trusted by the containers: either issued by a public
CA, or the private root added to the image's trust store by the platform.

### Behind the ingress

The ingress ends TLS, so the IDP, the Shell and the .NET BFFs must learn the original scheme and
client address from `X-Forwarded-Proto` / `X-Forwarded-For` (otherwise the IDP publishes `http://`
endpoints and a BFF sends `http://` sign-in redirect URIs, which the IDP refuses). They trust those
headers **only from the ingress**: list its addresses as `ForwardedHeaders__KnownNetworks__0`
(CIDR, e.g. the ingress subnet) or `ForwardedHeaders__KnownProxies__0` (IP). Nothing listed = no
forwarded header is trusted (local runs). The Host header is never taken from a forwarded header;
`AllowedHosts` checks the one the ingress passes on
([ProxyForwardedHeaders.cs](../src/Common/WebUtilities/Security/ProxyForwardedHeaders.cs)).

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
| The KYC BFF (NestJS) listens on HTTPS with its own certificate, and express is not told to trust the ingress (`trust proxy`) | Its container needs a certificate, or its `Secure` session cookie is not set behind a TLS-ending ingress | With its image: plain HTTP behind the ingress, `trust proxy` limited to the ingress's addresses (like `ForwardedHeaders`) |
| The BFF validates that API addresses are `https`, but the API containers listen on HTTP | Pod-to-pod calls need TLS from the platform | Choose at deployment: mesh mTLS with https service addresses, or Kestrel TLS from a mounted certificate (`ASPNETCORE_URLS=https://+:8443`, `ASPNETCORE_Kestrel__Certificates__Default__Path`); no code change either way |
| Base images are pinned by tag, not by digest | A re-pushed tag could change an image between two builds | Pin `@sha256:` digests (Dependabot keeps them current) |

## Adding an image

1. Copy the closest Dockerfile (API / BFF with client-app / worker / Node service). Change the
   project paths and the `COPY` lines of the shared projects it references (`ProjectReference`).
2. Move any connection string with a password from `appsettings.json` to
   `appsettings.Development.json`, and record it in this page's run-time contract.
3. Add the image to the matrix in [container-images.yml](../.github/workflows/container-images.yml)
   and its folder to [dependabot.yml](../.github/dependabot.yml).
4. Add a "Container image" row to the project README's "Security controls" table.