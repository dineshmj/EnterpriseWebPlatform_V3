# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/PartnerProducts/Worker/Dockerfile -t ewp/partner-products-worker .

# ┌─ :: ARCHITECTURE + SECURITY :: Partner Products Worker (Temporal) container image ──────────────
# │ Why : The worker runs the Digital Gold purchase workflow and listens to payment events with
# │       its own machine client and Kafka user; the image must be built from the committed
# │       lockfile and carry none of their secrets.
# │ How : Three stages. (1) Node compiles the TypeScript from the lockfile (tsc). (2) A second
# │       install takes the production dependencies only (the Kafka client's install script fetches
# │       its prebuilt librdkafka binding). (3) A slim Node runtime without npm, npx, corepack or
# │       yarn runs dist/main.js as the non-root "node" user; its health probes answer on plain
# │       HTTP 8080 (PARTNER_PRODUCTS_WORKER_PLAIN_HTTP=true). Secrets, the Kafka CA file and every
# │       address come from the environment at run time.
# │ Ref : doc/Containers.md | OWASP A05, A06, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.21.0-bookworm-slim AS build
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/PartnerProducts/Worker
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml (with the overrides and install-script rules in
# │ pnpm-workspace.yaml); the build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/PartnerProducts/Worker/package.json \
     src/Microservices/PartnerProducts/Worker/pnpm-lock.yaml \
     src/Microservices/PartnerProducts/Worker/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/Microservices/PartnerProducts/Worker/ ./
RUN pnpm run build

FROM node:24.21.0-bookworm-slim AS deps
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/PartnerProducts/Worker
COPY src/Microservices/PartnerProducts/Worker/package.json \
     src/Microservices/PartnerProducts/Worker/pnpm-lock.yaml \
     src/Microservices/PartnerProducts/Worker/pnpm-workspace.yaml ./
# Production dependencies only: no compiler or type definitions in the running image.
RUN pnpm install --frozen-lockfile --prod

FROM node:24.21.0-bookworm-slim AS runtime
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ No package manager in the running image: nothing can be installed into a running container,
# │ and npm's own dependencies are not there to be vulnerable.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
RUN rm -rf /usr/local/lib/node_modules /usr/local/bin/npm /usr/local/bin/npx /usr/local/bin/corepack \
           /usr/local/bin/yarn /usr/local/bin/yarnpkg /opt/yarn-*

ENV NODE_ENV=production \
    PARTNER_PRODUCTS_WORKER_PLAIN_HTTP=true \
    PARTNER_PRODUCTS_WORKER_HEALTH_PORT=8080

WORKDIR /app/src/Microservices/PartnerProducts/Worker
# Owned by root: the "node" user reads and runs the files but cannot change them.
COPY --from=deps /app/src/Microservices/PartnerProducts/Worker/node_modules/ ./node_modules/
COPY --from=build /app/src/Microservices/PartnerProducts/Worker/dist/ ./dist/
COPY --from=build /app/src/Microservices/PartnerProducts/Worker/package.json ./

EXPOSE 8080
# The image's "node" user, by number (1000), so Kubernetes runAsNonRoot can verify it.
USER 1000

# No HEALTHCHECK: the orchestrator probes /health/*, as for the .NET images.
ENTRYPOINT ["node", "dist/main.js"]