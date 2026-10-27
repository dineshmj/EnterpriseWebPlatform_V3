# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Notifications/API/Dockerfile -t ewp/notifications-api .

# ┌─ :: ARCHITECTURE + SECURITY :: Notifications API container image ───────────────────────────────
# │ Why : The image is what runs in every environment: the same bytes everywhere, no secret inside,
# │       as little as possible for an attacker who gets in.
# │ How : Multi-stage: the SDK image restores and publishes; only the published output goes into a
# │       chiseled ASP.NET runtime (no shell, no package manager, non-root "app" 1654). Plain HTTP on
# │       8080; TLS ends at the ingress / service mesh. Settings and secrets come from the
# │       environment at run time (doc/Containers.md, run-time contract).
# │ Ref : doc/Containers.md | OWASP A05, A08 | CIS Docker Benchmark 4.1
# └─────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Microservices/Notifications/API/EnterpriseWebPlatform.Notifications.Api.csproj src/Microservices/Notifications/API/
RUN dotnet restore src/Microservices/Notifications/API/EnterpriseWebPlatform.Notifications.Api.csproj --runtime linux-x64

COPY src/Common/Observability/ src/Common/Observability/
COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Microservices/Notifications/API/ src/Microservices/Notifications/API/
RUN dotnet publish src/Microservices/Notifications/API/EnterpriseWebPlatform.Notifications.Api.csproj \
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
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.Notifications.Api.dll"]