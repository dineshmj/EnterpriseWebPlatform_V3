# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/CustomerKyc/BFF.Web/Dockerfile -t ewp/customer-kyc-bff-web .

# ┌─ :: ARCHITECTURE + SECURITY :: Customer KYC BFF (NestJS + its micro front end) container image ───
# │ Why : The BFF serves the KYC micro front end and holds the officer's session; the image must
# │       build the front end from source - never from a developer's local export - and carry no
# │       client secret, session key or certificate.
# │ How : Four stages. (1) Node builds the Next.js static export from its lockfile. (2) Node
# │       compiles the NestJS BFF. (3) A second install takes its production dependencies only.
# │       (4) A slim Node runtime without npm, npx, corepack or yarn runs dist/main.js as the
# │       non-root "node" user on plain HTTP 8080 (KYC_BFF_PLAIN_HTTP=true) and serves the export;
# │       TLS ends at the ingress, which KYC_BFF_TRUSTED_PROXIES must name (the BFF refuses to
# │       start without it). Secrets and every address come from the environment at run time.
# │ Ref : doc/Containers.md | Features 2.1.1, 2.3 | OWASP A05, A06, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.21.0-bookworm-slim AS client
ARG PNPM_VERSION=12.11.2
# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────────────
# │ No environment's settings in the export: the MFE reads the Shell origin at run time from
# │ this BFF (/ewp-runtime-config.js, its KYC_BFF_SHELL_ORIGIN setting), so one image serves every
# │ environment.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm install --global "pnpm@${PNPM_VERSION}"

WORKDIR /app
# The design tokens the front end imports from outside its own folder (globals.css).
COPY src/Common/DesignSystem/ src/Common/DesignSystem/

WORKDIR /app/src/Microservices/CustomerKyc/BFF.Web/client-app
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml (with the overrides and install-script rules in
# │ pnpm-workspace.yaml); the build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/CustomerKyc/BFF.Web/client-app/package.json \
     src/Microservices/CustomerKyc/BFF.Web/client-app/pnpm-lock.yaml \
     src/Microservices/CustomerKyc/BFF.Web/client-app/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/Microservices/CustomerKyc/BFF.Web/client-app/ ./
RUN pnpm run build

FROM node:24.21.0-bookworm-slim AS build
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/CustomerKyc/BFF.Web
COPY src/Microservices/CustomerKyc/BFF.Web/package.json \
     src/Microservices/CustomerKyc/BFF.Web/pnpm-lock.yaml \
     src/Microservices/CustomerKyc/BFF.Web/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
# The BFF's own sources only (the front end comes from the "client" stage).
COPY src/Microservices/CustomerKyc/BFF.Web/nest-cli.json src/Microservices/CustomerKyc/BFF.Web/tsconfig.json ./
COPY src/Microservices/CustomerKyc/BFF.Web/src/ ./src/
RUN pnpm run build

FROM node:24.21.0-bookworm-slim AS deps
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/CustomerKyc/BFF.Web
COPY src/Microservices/CustomerKyc/BFF.Web/package.json \
     src/Microservices/CustomerKyc/BFF.Web/pnpm-lock.yaml \
     src/Microservices/CustomerKyc/BFF.Web/pnpm-workspace.yaml ./
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
    KYC_BFF_PLAIN_HTTP=true \
    KYC_BFF_PORT=8080 \
    KYC_BFF_STATIC_ROOT=/app/src/Microservices/CustomerKyc/BFF.Web/client-app/out

WORKDIR /app/src/Microservices/CustomerKyc/BFF.Web
# Owned by root: the "node" user reads and runs the files but cannot change them.
COPY --from=deps /app/src/Microservices/CustomerKyc/BFF.Web/node_modules/ ./node_modules/
COPY --from=build /app/src/Microservices/CustomerKyc/BFF.Web/dist/ ./dist/
COPY --from=build /app/src/Microservices/CustomerKyc/BFF.Web/package.json ./
COPY --from=client /app/src/Microservices/CustomerKyc/BFF.Web/client-app/out/ ./client-app/out/

EXPOSE 8080
# The image's "node" user, by number (1000), so Kubernetes runAsNonRoot can verify it.
USER 1000

# No HEALTHCHECK: the orchestrator probes /health/*, as for the .NET images.
ENTRYPOINT ["node", "dist/main.js"]