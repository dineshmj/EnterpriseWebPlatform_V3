# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Shell/Dockerfile -t ewp/shell .

# ┌─ :: ARCHITECTURE + SECURITY :: Shell (host page + its BFF) container image ─────────────────────
# │ Why : The Shell is the page every staff member opens first: it frames the micro front ends,
# │       holds the browser's session and reads the menu. Its image must build the front end from
# │       source - never from a developer's local export - and carry no client secret or password.
# │ How : Three stages. (1) Node builds the Next.js static export from the committed lockfile
# │       (CompileAndExportBFFClients_V3.ps1 locally, without the copy to wwwroot). (2) The .NET
# │       SDK publishes the Shell with that export in wwwroot. (3) A chiseled, non-root ASP.NET
# │       runtime runs it on plain HTTP 8080; TLS ends at the ingress. Oidc__ClientSecret,
# │       ConnectionStrings__MenuDbConnection, ConnectionStrings__BffStateDbConnection and the
# │       origins (ShellOrigin, MfeOrigins, NotificationsApiBaseUrl) come from the environment.
# │ Ref : doc/Containers.md | Features 2.3.3, 2.4.5 | OWASP A05, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS client
ARG PNPM_VERSION=12.11.2

# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────────────
# │ No environment's settings in the export: the Shell page gets the menu - with each MFE's
# │ address, from the menu database - from its own BFF at run time, so one image serves every
# │ environment.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm install --global "pnpm@${PNPM_VERSION}"

WORKDIR /src/src/Shell/client-app
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml (with the overrides in pnpm-workspace.yaml); the
# │ build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Shell/client-app/package.json \
     src/Shell/client-app/pnpm-lock.yaml \
     src/Shell/client-app/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/Shell/client-app/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/Shell/EnterpriseWebPlatform.BSS.BFFWeb.csproj src/Shell/
RUN dotnet restore src/Shell/EnterpriseWebPlatform.BSS.BFFWeb.csproj --runtime linux-x64

COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Common/Observability/ src/Common/Observability/
COPY src/Shell/ src/Shell/
# The export goes where "pnpm run export" puts it locally; the Shell serves it with UseStaticFiles.
COPY --from=client /src/src/Shell/client-app/out/ src/Shell/wwwroot/
RUN dotnet publish src/Shell/EnterpriseWebPlatform.BSS.BFFWeb.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS runtime
WORKDIR /app

# Owned by root: the "app" user reads and runs the files but cannot change them.
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/*.
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.BSS.BFFWeb.dll"]