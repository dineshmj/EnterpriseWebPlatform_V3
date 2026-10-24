# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Microservices/PartnerProducts/API/Dockerfile -t ewp/partner-products-api .

# ┌─ :: ARCHITECTURE + SECURITY :: Partner Products API container image ────────────────────────────
# │ Why : The image is what runs in every environment: the same bytes everywhere, no secret inside,
# │       as little as possible for an attacker who gets in.
# │ How : Multi-stage: the SDK image restores and publishes; only the published output goes into a
# │       chiseled ASP.NET runtime (no shell, no package manager, non-root "app" 1654). Plain HTTP on
# │       8080; TLS ends at the ingress / service mesh. Settings and secrets come from the
# │       environment at run time (doc/Containers.md, run-time contract).
# │       EWP's mTLS client and request-signing certificates (PartnerBank:*CertificatePath and their
# │       password files) are mounted from the secret store - never in the image.
# │ Ref : doc/Containers.md | OWASP A05, A08 | CIS Docker Benchmark 4.1
# └─────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/AsyncWorkflows/Infrastructure/Kafka/EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka.csproj src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Microservices/PartnerProducts/API/EnterpriseWebPlatform.PartnerProducts.Api.csproj src/Microservices/PartnerProducts/API/
RUN dotnet restore src/Microservices/PartnerProducts/API/EnterpriseWebPlatform.PartnerProducts.Api.csproj --runtime linux-x64

COPY src/AsyncWorkflows/Infrastructure/Kafka/ src/AsyncWorkflows/Infrastructure/Kafka/
COPY src/Common/Observability/ src/Common/Observability/
COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Microservices/PartnerProducts/API/ src/Microservices/PartnerProducts/API/
RUN dotnet publish src/Microservices/PartnerProducts/API/EnterpriseWebPlatform.PartnerProducts.Api.csproj \
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
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.PartnerProducts.Api.dll"]