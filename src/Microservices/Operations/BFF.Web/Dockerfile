# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Operations/BFF.Web/Dockerfile -t ewp/operations-bff-web .

# ┌─ :: ARCHITECTURE + SECURITY :: Operations web (Next.js SPA + light BFF) container image ────────
# │ Why : The light BFF holds the user's session and calls its Journey API; the image must be
# │       built from source and the committed lockfile, carry no secret, and run with as little
# │       as possible besides the app itself.
# │ How : Three stages. (1) Node builds the Next.js app from the lockfile (dev dependencies
# │       included). (2) A second install takes the production dependencies only. (3) A distroless Node
# │       runtime (no shell, no package manager) runs server.mjs as the non-root user 65532
# │       on plain HTTP 8080 (OPERATIONS_WEB_PLAIN_HTTP=true); TLS ends at the ingress.
# │       Forwarded headers count only from OPERATIONS_WEB_TRUSTED_PROXIES.
# │       Client secret, session key, database URL and every address come from the environment
# │       at run time.
# │ Ref : doc/Containers.md | Features 2.3.3 | OWASP A05, A06, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS build
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

WORKDIR /app/src/Microservices/Operations/BFF.Web
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml; the build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/Operations/BFF.Web/package.json \
     src/Microservices/Operations/BFF.Web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/Microservices/Operations/BFF.Web/ ./
# The build cache is not needed to run the app.
RUN pnpm run build && rm -rf .next/cache

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS deps
ARG PNPM_VERSION=12.11.2
RUN npm install --global "pnpm@${PNPM_VERSION}"
WORKDIR /app/src/Microservices/Operations/BFF.Web
COPY src/Microservices/Operations/BFF.Web/package.json \
     src/Microservices/Operations/BFF.Web/pnpm-lock.yaml ./
# Production dependencies only: no compiler, bundler or type definitions in the running image.
RUN pnpm install --frozen-lockfile --prod

FROM gcr.io/distroless/nodejs24-debian13:nonroot@sha256:cb606325b7ec4af55462cb0b424a53b452a5b4a894ffe841fcf1048d216fb9f8 AS runtime
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Distroless Node: the Node runtime and the few Debian libraries it needs - no shell, no package
# │ manager (no apt, npm, npx, corepack or yarn), like the chiseled .NET images. Nothing can be
# │ installed into or run through a shell in a running container. Its user "nonroot" (65532).
# │ The image is rebuilt with every Debian fix; the digest pin above is moved forward by
# │ Dependabot, and the scan in CI gates each move. Native modules are built in the trixie stages
# │ above, the same Debian 13 the runtime is made from.
# └──────────────────────────────────────────────────────────────────────────────────────────────────

ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    OPERATIONS_WEB_PLAIN_HTTP=true \
    OPERATIONS_WEB_PORT=8080

WORKDIR /app/src/Microservices/Operations/BFF.Web
# Owned by root: the "nonroot" user reads and runs the files but cannot change them.
COPY --from=deps /app/src/Microservices/Operations/BFF.Web/node_modules/ ./node_modules/
COPY --from=build /app/src/Microservices/Operations/BFF.Web/.next/ ./.next/
# Next.js looks for the app folder when its server starts (it serves the build in .next).
COPY --from=build /app/src/Microservices/Operations/BFF.Web/app/ ./app/
COPY --from=build /app/src/Microservices/Operations/BFF.Web/package.json \
                  /app/src/Microservices/Operations/BFF.Web/next.config.ts \
                  /app/src/Microservices/Operations/BFF.Web/server.mjs \
                  /app/src/Microservices/Operations/BFF.Web/forwarded-headers.mjs ./

EXPOSE 8080
# The image's "nonroot" user, by number (65532), so Kubernetes runAsNonRoot can verify it.
USER 65532

# No HEALTHCHECK: the orchestrator probes /health/live and /health/ready, as for the .NET images.
ENTRYPOINT ["/nodejs/bin/node", "server.mjs"]