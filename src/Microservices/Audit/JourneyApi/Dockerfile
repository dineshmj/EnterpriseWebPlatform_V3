# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Audit/JourneyApi/Dockerfile -t ewp/audit-journey-api .

# ┌─ :: ARCHITECTURE + SECURITY :: Audit Journey API (NestJS) container image ──────────────────────
# │ Why : The Journey API composes Domain API calls for its BFF with exchanged tokens; the image
# │       must be built from the committed lockfile and carry no client secret.
# │ How : Three stages. (1) Node compiles the TypeScript from the lockfile (nest build).
# │       (2) A second install takes the production dependencies only. (3) A distroless Node runtime
# │       (no shell, no package manager) runs dist/main.js as the non-root user 65532 on
# │       plain HTTP 8080 (AUDIT_JOURNEY_PLAIN_HTTP=true);
# │       TLS between pods comes from the platform (service mesh mTLS).
# │       Secrets and every address come from the environment at run time.
# │ Ref : doc/Containers.md | OWASP A05, A06, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS build
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

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS deps
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/Audit/JourneyApi
COPY src/Microservices/Audit/JourneyApi/package.json \
     src/Microservices/Audit/JourneyApi/pnpm-lock.yaml \
     src/Microservices/Audit/JourneyApi/pnpm-workspace.yaml ./
# Production dependencies only: no compiler or type definitions in the running image.
RUN pnpm install --frozen-lockfile --prod

FROM gcr.io/distroless/nodejs24-debian13:nonroot@sha256:78f07ef1b5ea71ad7c055df15d0dd8997f2d288f9158623c4d4ae3d765b7d50d AS runtime
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Distroless Node: the Node runtime and the few Debian libraries it needs - no shell, no package
# │ manager (no apt, npm, npx, corepack or yarn), like the chiseled .NET images. Nothing can be
# │ installed into or run through a shell in a running container. Its user "nonroot" (65532).
# │ The image is rebuilt with every Debian fix; the digest pin above is moved forward by
# │ Dependabot, and the scan in CI gates each move. Native modules are built in the trixie stages
# │ above, the same Debian 13 the runtime is made from.
# └──────────────────────────────────────────────────────────────────────────────────────────────────

ENV NODE_ENV=production \
    AUDIT_JOURNEY_PLAIN_HTTP=true \
    AUDIT_JOURNEY_PORT=8080

WORKDIR /app/src/Microservices/Audit/JourneyApi
# Owned by root: the "nonroot" user reads and runs the files but cannot change them.
COPY --from=deps /app/src/Microservices/Audit/JourneyApi/node_modules/ ./node_modules/
COPY --from=build /app/src/Microservices/Audit/JourneyApi/dist/ ./dist/
COPY --from=build /app/src/Microservices/Audit/JourneyApi/package.json ./

EXPOSE 8080
# The image's "nonroot" user, by number (65532), so Kubernetes runAsNonRoot can verify it.
USER 65532

# No HEALTHCHECK: the orchestrator probes /health/*, as for the .NET images.
ENTRYPOINT ["/nodejs/bin/node", "dist/main.js"]