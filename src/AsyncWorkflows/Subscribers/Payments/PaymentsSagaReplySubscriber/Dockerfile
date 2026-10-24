# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/Dockerfile -t ewp/payments-saga-reply-subscriber .

# ┌─ :: ARCHITECTURE + SECURITY :: Payments saga reply subscriber container image ────────────────
# │ Why : A Kafka worker holds a Kafka identity and an M2M client of its own; its image must
# │       carry neither, and must be as small and locked down as the APIs'.
# │ How : Same pattern as the APIs (doc/Containers.md): SDK stage publishes, chiseled non-root
# │       runtime runs it. The ASP.NET runtime (not the plain .NET one) because the worker serves
# │       /health/live, /health/ready and /metrics from a small Kestrel listener on Health:Urls,
# │       set to all interfaces on 8080 here so the orchestrator can probe it. The Kafka password
# │       and client secret come from the environment (Kafka__SaslPassword,
# │       PaymentsSagaReplySubscriber__ClientSecret); the Kafka trust root is a mounted file
# │       (Kafka__SslCaLocation).
# │ Ref : doc/Containers.md | Features 2.4.6 | OWASP A05, A08 | Threat-Model R-10
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/AsyncWorkflows/Infrastructure/Kafka/EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka.csproj src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/AsyncWorkflows/Infrastructure/Subscribers/EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers.csproj src/AsyncWorkflows/Infrastructure/Subscribers/
COPY src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/PaymentsSagaReplySubscriber.csproj src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/
RUN dotnet restore src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/PaymentsSagaReplySubscriber.csproj --runtime linux-x64

COPY src/Common/Observability/ src/Common/Observability/
COPY src/AsyncWorkflows/Infrastructure/Kafka/ src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/AsyncWorkflows/Infrastructure/Subscribers/ src/AsyncWorkflows/Infrastructure/Subscribers/
COPY src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/ src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/
RUN dotnet publish src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/PaymentsSagaReplySubscriber.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df AS runtime
WORKDIR /app

# Owned by root: the "app" user reads and runs the files but cannot change them.
COPY --from=build /app ./

# ┌─ OBS ────────────────────────────────────────────────────────────────────────────────────────────
# │ The health listener binds every interface inside the container (appsettings.json says
# │ localhost:5109 for a local run, where nothing else may reach it).
# └──────────────────────────────────────────────────────────────────────────────────────────────────
ENV Health__Urls=http://+:8080
EXPOSE 8080
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/*.
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Payments.PaymentsSagaReplySubscriber.dll"]