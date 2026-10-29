# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Compliance/BFF.Web/Dockerfile -t ewp/compliance-bff-web .

# ┌─ :: ARCHITECTURE + SECURITY :: Compliance BFF (+ its micro front end) container image ──────────
# │ Why : The BFF serves the Compliance micro front end and holds the browser's session; the image
# │       must build the front end from source - never from a developer's local export - and
# │       carry no client secret or connection string.
# │ How : Three stages. (1) Node builds the Next.js static export from the committed lockfile
# │       (the same steps as CompileAndExportBFFClients_V3.ps1, without the copy to wwwroot).
# │       (2) The .NET SDK publishes the BFF with that export in wwwroot. (3) A chiseled,
# │       non-root ASP.NET runtime runs it on plain HTTP 8080; TLS ends at the ingress.
# │       ConnectionStrings__BffStateDbConnection and ShellOrigin come from the environment at
# │       run time; the private_key_jwt key (Oidc__ClientKey__*) is a file mounted at run time.
# │ Ref : doc/Containers.md | Features 2.3.3, 2.4.5 | OWASP A05, A08
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM node:24.22.1-trixie-slim@sha256:43b9ba1142b8dcf33fa8f922ca1be85714adc444e0586c7cf7726daaeabc1bea AS client
ARG PNPM_VERSION=12.11.2

# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────────────
# │ No environment's settings in the export: the MFE reads the Shell origin at run time from
# │ this BFF (/ewp-runtime-config.js, its ShellOrigin setting), so one image serves every
# │ environment.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm install --global --ignore-scripts "pnpm@${PNPM_VERSION}"

WORKDIR /src
# The design tokens the front end imports from outside its own folder (globals.css).
COPY src/Common/DesignSystem/ src/Common/DesignSystem/

WORKDIR /src/src/Microservices/Compliance/BFF.Web/client-app
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml (with the overrides in pnpm-workspace.yaml); the
# │ build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/Compliance/BFF.Web/client-app/package.json \
     src/Microservices/Compliance/BFF.Web/client-app/pnpm-lock.yaml \
     src/Microservices/Compliance/BFF.Web/client-app/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile --ignore-scripts
COPY src/Microservices/Compliance/BFF.Web/client-app/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/Microservices/Compliance/BFF.Web/EnterpriseWebPlatform.Compliance.Bff.Web.csproj src/Microservices/Compliance/BFF.Web/
RUN dotnet restore src/Microservices/Compliance/BFF.Web/EnterpriseWebPlatform.Compliance.Bff.Web.csproj --runtime linux-x64

COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Common/Observability/ src/Common/Observability/
COPY src/Microservices/Compliance/BFF.Web/ src/Microservices/Compliance/BFF.Web/
# The export goes where export-to-bff.ps1 puts it locally; the BFF serves it with UseStaticFiles.
COPY --from=client /src/src/Microservices/Compliance/BFF.Web/client-app/out/ src/Microservices/Compliance/BFF.Web/wwwroot/
RUN dotnet publish src/Microservices/Compliance/BFF.Web/EnterpriseWebPlatform.Compliance.Bff.Web.csproj \
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
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.Compliance.Bff.Web.dll"]