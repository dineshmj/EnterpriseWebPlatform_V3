# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Dockerfile -t ewp/customer-outbox-publisher .

# ┌─ :: ARCHITECTURE + SECURITY :: Customer Outbox publisher container image ───────────────────────
# │ Why : A Kafka worker holds a Kafka identity and an M2M client of its own; its image must carry
# │       neither, and be as small and locked down as the APIs'.
# │ How : Same pattern as the APIs: SDK stage publishes, chiseled non-root ASP.NET runtime runs it
# │       (the worker serves /health/* and /metrics from Kestrel on Health:Urls - all interfaces on
# │       8080 here). Kafka password and client secret from the environment; the Kafka trust root is
# │       a mounted file (Kafka__SslCaLocation).
# │ Ref : doc/Containers.md | OWASP A05, A08 | CIS Docker Benchmark 4.1
# └─────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/AsyncWorkflows/Contracts/EnterpriseWebPlatform.BSS.AsyncWorkflows.Contracts.csproj src/AsyncWorkflows/Contracts/
COPY src/AsyncWorkflows/Infrastructure/Kafka/EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka.csproj src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/CustomerOutboxPublisher.csproj src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/
RUN dotnet restore src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/CustomerOutboxPublisher.csproj --runtime linux-x64

COPY src/AsyncWorkflows/Contracts/ src/AsyncWorkflows/Contracts/
COPY src/AsyncWorkflows/Infrastructure/Kafka/ src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/Common/Observability/ src/Common/Observability/
COPY src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/ src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/
RUN dotnet publish src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/CustomerOutboxPublisher.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra AS runtime
WORKDIR /app

# Owned by root: the "app" user reads and runs the files but cannot change them.
COPY --from=build /app ./

# Health listener on every interface inside the container (appsettings.json: localhost only).
ENV Health__Urls=http://+:8080
EXPOSE 8080
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/*.
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.dll"]