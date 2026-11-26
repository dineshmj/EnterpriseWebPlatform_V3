# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/DocumentsManagement/API/Dockerfile -t ewp/documents-management-api .

# ┌─ :: ARCHITECTURE + SECURITY :: Documents Management API container image ────────────────────────
# │ Why : The image is what runs in every environment: the same bytes everywhere, no secret inside,
# │       as little as possible for an attacker who gets in.
# │ How : Multi-stage: the SDK image restores and publishes; only the published output goes into a
# │       chiseled ASP.NET runtime (no shell, no package manager, non-root "app" 1654). Plain HTTP on
# │       8080; TLS ends at the ingress / service mesh. Settings and secrets come from the
# │       environment at run time (doc/Containers.md, run-time contract).
# │       DocumentStorage:RootPath must be a mounted volume (the encrypted documents); /app itself
# │       is read-only. The encryption key ring comes from the environment, never the volume.
# │ Ref : doc/Containers.md | OWASP A05, A08 | CIS Docker Benchmark 4.1
# └─────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Microservices/DocumentsManagement/API/EnterpriseWebPlatform.BSS.Microservices.DocumentsManagement.Api.csproj src/Microservices/DocumentsManagement/API/
RUN dotnet restore src/Microservices/DocumentsManagement/API/EnterpriseWebPlatform.BSS.Microservices.DocumentsManagement.Api.csproj --runtime linux-x64

COPY src/Common/Observability/ src/Common/Observability/
COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Microservices/DocumentsManagement/API/ src/Microservices/DocumentsManagement/API/
RUN dotnet publish src/Microservices/DocumentsManagement/API/EnterpriseWebPlatform.BSS.Microservices.DocumentsManagement.Api.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled-extra@sha256:c8dd08a50d8ffd19bd8da72edee05a7ac15b486a5b1869cf46fdb370bf1fbc76 AS runtime
WORKDIR /app

# Owned by root: the "app" user reads and runs the files but cannot change them.
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/*.
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.BSS.Microservices.DocumentsManagement.Api.dll"]