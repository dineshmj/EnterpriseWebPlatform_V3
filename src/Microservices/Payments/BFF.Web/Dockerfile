# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Payments/BFF.Web/Dockerfile -t ewp/payments-bff-web .

# ┌─ :: ARCHITECTURE + SECURITY :: Payments BFF (+ its micro front end) container image ──────────
# │ Why : The BFF serves the Payments micro front end and holds the browser's session; the image
# │       must build the front end from source - never from a developer's local export - and
# │       carry no client secret or connection string.
# │ How : Three stages. (1) Node builds the Next.js static export from the committed lockfile
# │       (the same steps as CompileAndExportBFFClients_V3.ps1, without the copy to wwwroot).
# │       (2) The .NET SDK publishes the BFF with that export in wwwroot. (3) A chiseled,
# │       non-root ASP.NET runtime runs it on plain HTTP 8080; TLS ends at the ingress.
# │       Oidc__ClientSecret, ConnectionStrings__BffStateDbConnection and ShellOrigin come from
# │       the environment at run time.
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
RUN npm install --global "pnpm@${PNPM_VERSION}"

WORKDIR /src
# The design tokens the front end imports from outside its own folder (globals.css).
COPY src/Common/DesignSystem/ src/Common/DesignSystem/

WORKDIR /src/src/Microservices/Payments/BFF.Web/client-app
# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Exactly the versions in pnpm-lock.yaml (with the overrides in pnpm-workspace.yaml); the
# │ build fails instead of resolving anything new.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY src/Microservices/Payments/BFF.Web/client-app/package.json \
     src/Microservices/Payments/BFF.Web/client-app/pnpm-lock.yaml \
     src/Microservices/Payments/BFF.Web/client-app/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/Microservices/Payments/BFF.Web/client-app/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/Microservices/Payments/BFF.Web/EnterpriseWebPlatform.Payments.Bff.Web.csproj src/Microservices/Payments/BFF.Web/
RUN dotnet restore src/Microservices/Payments/BFF.Web/EnterpriseWebPlatform.Payments.Bff.Web.csproj --runtime linux-x64

COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Common/Observability/ src/Common/Observability/
COPY src/Microservices/Payments/BFF.Web/ src/Microservices/Payments/BFF.Web/
# The export goes where export-to-bff.ps1 puts it locally; the BFF serves it with UseStaticFiles.
COPY --from=client /src/src/Microservices/Payments/BFF.Web/client-app/out/ src/Microservices/Payments/BFF.Web/wwwroot/
RUN dotnet publish src/Microservices/Payments/BFF.Web/EnterpriseWebPlatform.Payments.Bff.Web.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df AS runtime
WORKDIR /app

# Owned by root: the "app" user reads and runs the files but cannot change them.
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/*.
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.Payments.Bff.Web.dll"]