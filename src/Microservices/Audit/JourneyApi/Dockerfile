# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Audit/JourneyApi/Dockerfile -t ewp/audit-journey-api .

# ┌─ :: ARCHITECTURE + SECURITY :: Audit Journey API (NestJS) container image ──────────────────────
# │ Why : The Journey API composes Domain API calls for its BFF with exchanged tokens; the image
# │       must be built from the committed lockfile and carry no client secret.
# │ How : Three stages. (1) Node compiles the TypeScript from the lockfile (nest build).
# │       (2) A second install takes the production dependencies only. (3) A slim Node runtime
# │       without npm, npx, corepack or yarn runs dist/main.js as the non-root "node" user on
# │       plain HTTP 8080 (AUDIT_JOURNEY_PLAIN_HTTP=true);
# │       TLS between pods comes from the platform (service mesh mTLS).
# │       Secrets and every address come from the environment at run time.
# │ Ref : doc/Containers.md | OWASP A05, A06, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.22.1-bookworm-slim AS build
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/Audit/JourneyApi
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml (with the overrides and install-script rules in
# │ pnpm-workspace.yaml); the build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/Audit/JourneyApi/package.json \
     src/Microservices/Audit/JourneyApi/pnpm-lock.yaml \
     src/Microservices/Audit/JourneyApi/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/Microservices/Audit/JourneyApi/ ./
RUN pnpm run build

FROM node:24.22.1-bookworm-slim AS deps
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/Audit/JourneyApi
COPY src/Microservices/Audit/JourneyApi/package.json \
     src/Microservices/Audit/JourneyApi/pnpm-lock.yaml \
     src/Microservices/Audit/JourneyApi/pnpm-workspace.yaml ./
# Production dependencies only: no compiler or type definitions in the running image.
RUN pnpm install --frozen-lockfile --prod

FROM node:24.22.1-bookworm-slim AS runtime
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Debian security updates at build time: the official Node image is rebuilt only from time to
# │ time, so a fixed Debian package (e.g. perl-base, DLA-4821-1) can lag behind it and Trivy fails
# │ the build. Upgrading here takes every published fix on each rebuild; apt lists are removed so
# │ the layer stays small. Trade-off: the OS layer follows Debian, not only the pinned tag.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
RUN apt-get update \
 && apt-get upgrade -y --no-install-recommends \
 && rm -rf /var/lib/apt/lists/*
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ No package manager in the running image: nothing can be installed into a running container,
# │ and npm's own dependencies are not there to be vulnerable.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
RUN rm -rf /usr/local/lib/node_modules /usr/local/bin/npm /usr/local/bin/npx /usr/local/bin/corepack \
           /usr/local/bin/yarn /usr/local/bin/yarnpkg /opt/yarn-*

ENV NODE_ENV=production \
    AUDIT_JOURNEY_PLAIN_HTTP=true \
    AUDIT_JOURNEY_PORT=8080

WORKDIR /app/src/Microservices/Audit/JourneyApi
# Owned by root: the "node" user reads and runs the files but cannot change them.
COPY --from=deps /app/src/Microservices/Audit/JourneyApi/node_modules/ ./node_modules/
COPY --from=build /app/src/Microservices/Audit/JourneyApi/dist/ ./dist/
COPY --from=build /app/src/Microservices/Audit/JourneyApi/package.json ./

EXPOSE 8080
# The image's "node" user, by number (1000), so Kubernetes runAsNonRoot can verify it.
USER 1000

# No HEALTHCHECK: the orchestrator probes /health/*, as for the .NET images.
ENTRYPOINT ["node", "dist/main.js"]