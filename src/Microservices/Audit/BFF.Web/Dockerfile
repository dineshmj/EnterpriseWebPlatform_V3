# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Audit/BFF.Web/Dockerfile -t ewp/audit-bff-web .

# ┌─ :: ARCHITECTURE + SECURITY :: Audit web (Next.js SPA + light BFF) container image ─────────────
# │ Why : The light BFF holds the user's session and calls its Journey API; the image must be
# │       built from source and the committed lockfile, carry no secret, and run with as little
# │       as possible besides the app itself.
# │ How : Three stages. (1) Node builds the Next.js app from the lockfile (dev dependencies
# │       included). (2) A second install takes the production dependencies only. (3) A slim Node
# │       runtime without npm, npx, corepack or yarn runs server.mjs as the non-root "node" user
# │       on plain HTTP 8080 (AUDIT_WEB_PLAIN_HTTP=true); TLS ends at the ingress.
# │       Forwarded headers count only from AUDIT_WEB_TRUSTED_PROXIES.
# │       Client secret, session key, database URL and every address come from the environment
# │       at run time.
# │ Ref : doc/Containers.md | Features 2.3.3 | OWASP A05, A06, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.21.0-bookworm-slim AS build
ARG PNPM_VERSION=12.11.2
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm install --global "pnpm@${PNPM_VERSION}"

# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────────────
# │ The repository's folder layout under /app, in every stage: Turbopack builds with the src folder
# │ as its root (next.config.ts), and the server finds its output at the same paths it was built at.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
WORKDIR /app
# The design tokens the front end imports from outside its own folder (globals.css).
COPY src/Common/DesignSystem/ src/Common/DesignSystem/

WORKDIR /app/src/Microservices/Audit/BFF.Web
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml; the build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/Audit/BFF.Web/package.json \
     src/Microservices/Audit/BFF.Web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/Microservices/Audit/BFF.Web/ ./
# The build cache is not needed to run the app.
RUN pnpm run build && rm -rf .next/cache

FROM node:24.21.0-bookworm-slim AS deps
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/Audit/BFF.Web
COPY src/Microservices/Audit/BFF.Web/package.json \
     src/Microservices/Audit/BFF.Web/pnpm-lock.yaml ./
# Production dependencies only: no compiler, bundler or type definitions in the running image.
RUN pnpm install --frozen-lockfile --prod

FROM node:24.21.0-bookworm-slim AS runtime
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ No package manager in the running image: nothing can be installed into a running container,
# │ and npm's own dependencies are not there to be vulnerable.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
RUN rm -rf /usr/local/lib/node_modules /usr/local/bin/npm /usr/local/bin/npx /usr/local/bin/corepack \
           /usr/local/bin/yarn /usr/local/bin/yarnpkg /opt/yarn-*

ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    AUDIT_WEB_PLAIN_HTTP=true \
    AUDIT_WEB_PORT=8080

WORKDIR /app/src/Microservices/Audit/BFF.Web
# Owned by root: the "node" user reads and runs the files but cannot change them.
COPY --from=deps /app/src/Microservices/Audit/BFF.Web/node_modules/ ./node_modules/
COPY --from=build /app/src/Microservices/Audit/BFF.Web/.next/ ./.next/
# Next.js looks for the app folder when its server starts (it serves the build in .next).
COPY --from=build /app/src/Microservices/Audit/BFF.Web/app/ ./app/
COPY --from=build /app/src/Microservices/Audit/BFF.Web/package.json \
                  /app/src/Microservices/Audit/BFF.Web/next.config.ts \
                  /app/src/Microservices/Audit/BFF.Web/server.mjs \
                  /app/src/Microservices/Audit/BFF.Web/forwarded-headers.mjs ./

EXPOSE 8080
# The image's "node" user, by number (1000), so Kubernetes runAsNonRoot can verify it.
USER 1000

# No HEALTHCHECK: the orchestrator probes /health/live and /health/ready, as for the .NET images.
ENTRYPOINT ["node", "server.mjs"]