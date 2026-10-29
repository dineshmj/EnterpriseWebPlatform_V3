# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/PartnerProducts/Worker/Dockerfile -t ewp/partner-products-worker .

# ┌─ :: ARCHITECTURE + SECURITY :: Partner Products Worker (Temporal) container image ──────────────
# │ Why : The worker runs the Digital Gold purchase workflow and listens to payment events with
# │       its own machine client and Kafka user; the image must be built from the committed
# │       lockfile and carry none of their secrets.
# │ How : Three stages. (1) Node compiles the TypeScript from the lockfile (tsc). (2) A second
# │       install takes the production dependencies only (the Kafka client's install script fetches
# │       its prebuilt librdkafka binding). (3) A distroless Node runtime (no shell, no package manager)
# │       runs dist/main.js as the non-root user 65532; its health probes answer on plain
# │       HTTP 8080 (PARTNER_PRODUCTS_WORKER_PLAIN_HTTP=true). Secrets, the Kafka CA file and every
# │       address come from the environment at run time.
# │ Ref : doc/Containers.md | OWASP A05, A06, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS build
ARG PNPM_VERSION=12.11.2
RUN npm install --global --ignore-scripts "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/PartnerProducts/Worker
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml (with the overrides in pnpm-workspace.yaml); the build
# │ fails instead of resolving anything new. No install script runs (--ignore-scripts, also for
# │ pnpm itself): a compromised package cannot execute code while the image is built.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/PartnerProducts/Worker/package.json \
     src/Microservices/PartnerProducts/Worker/pnpm-lock.yaml \
     src/Microservices/PartnerProducts/Worker/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile --ignore-scripts
COPY src/Microservices/PartnerProducts/Worker/ ./
RUN pnpm run build

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS deps
ARG PNPM_VERSION=12.11.2
RUN npm install --global --ignore-scripts "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/PartnerProducts/Worker
COPY src/Microservices/PartnerProducts/Worker/package.json \
     src/Microservices/PartnerProducts/Worker/pnpm-lock.yaml \
     src/Microservices/PartnerProducts/Worker/pnpm-workspace.yaml ./
# Production dependencies only: no compiler or type definitions in the running image.
RUN pnpm install --frozen-lockfile --prod --ignore-scripts
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ The ONE install step allowed to run: the Kafka client fetches its native library (librdkafka).
# │ Named here explicitly, so no other package's script can slip in with it.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
RUN pnpm rebuild @confluentinc/kafka-javascript

FROM gcr.io/distroless/nodejs24-debian13:nonroot@sha256:a6c34457f6da4295807e051411c7783739c65b997a324fed14e4fa292615ae91 AS runtime
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Distroless Node: the Node runtime and the few Debian libraries it needs - no shell, no package
# │ manager (no apt, npm, npx, corepack or yarn), like the chiseled .NET images. Nothing can be
# │ installed into or run through a shell in a running container. Its user "nonroot" (65532).
# │ The image is rebuilt with every Debian fix; the digest pin above is moved forward by
# │ Dependabot, and the scan in CI gates each move. Native modules are built in the trixie stages
# │ above, the same Debian 13 the runtime is made from.
# └──────────────────────────────────────────────────────────────────────────────────────────────────

ENV NODE_ENV=production \
    PARTNER_PRODUCTS_WORKER_PLAIN_HTTP=true \
    PARTNER_PRODUCTS_WORKER_HEALTH_PORT=8080

WORKDIR /app/src/Microservices/PartnerProducts/Worker
# Owned by root: the "nonroot" user reads and runs the files but cannot change them.
COPY --from=deps /app/src/Microservices/PartnerProducts/Worker/node_modules/ ./node_modules/
COPY --from=build /app/src/Microservices/PartnerProducts/Worker/dist/ ./dist/
COPY --from=build /app/src/Microservices/PartnerProducts/Worker/package.json ./

EXPOSE 8080
# The image's "nonroot" user, by number (65532), so Kubernetes runAsNonRoot can verify it.
USER 65532

# No HEALTHCHECK: the orchestrator probes /health/*, as for the .NET images.
ENTRYPOINT ["/nodejs/bin/node", "dist/main.js"]