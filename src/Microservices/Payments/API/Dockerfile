# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/Payments/API/Dockerfile -t ewp/payments-api .

# ┌─ :: ARCHITECTURE + SECURITY :: Payments API container image ──────────────────────────────────
# │ Why : The image is what runs in every environment, so it must be the same bytes everywhere,
# │       carry no secret and give an attacker inside it as little as possible.
# │ How : Multi-stage: the SDK image restores and publishes; only the published output goes into
# │       a chiseled ASP.NET runtime (no shell, no package manager, non-root user "app" 1654).
# │       "-extra" adds ICU and time zones - the API formats amounts in en-AU. Plain HTTP on 8080
# │       inside the pod: TLS ends at the ingress / service mesh (mTLS between pods), as for
# │       Temporal (R-16). Settings and secrets come from the environment at run time.
# │ Ref : doc/Containers.md | Features 2.4.5 | OWASP A05, A08 | CIS Docker Benchmark 4.1
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src

# ┌─ PERF ───────────────────────────────────────────────────────────────────────────────────────────
# │ Restore from the project files alone: the restore layer is reused until a project file
# │ changes, not on every code change.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY Directory.Build.props ./
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/AsyncWorkflows/Infrastructure/Kafka/EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka.csproj src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/Microservices/Payments/API/EnterpriseWebPlatform.Payments.Api.csproj src/Microservices/Payments/API/
RUN dotnet restore src/Microservices/Payments/API/EnterpriseWebPlatform.Payments.Api.csproj --runtime linux-x64

COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Common/Observability/ src/Common/Observability/
COPY src/AsyncWorkflows/Infrastructure/Kafka/ src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/Microservices/Payments/API/ src/Microservices/Payments/API/
RUN dotnet publish src/Microservices/Payments/API/EnterpriseWebPlatform.Payments.Api.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df AS runtime
WORKDIR /app

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ The files stay owned by root: the "app" user can read and run them but not change them, so
# │ the container also runs with a read-only root file system.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────────────────
# │ No HEALTHCHECK: the chiseled image has no shell or curl. The orchestrator probes
# │ /health/live and /health/ready on 8080.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.Payments.Api.dll"]