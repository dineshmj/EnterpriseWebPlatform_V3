# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/Simulators/ScreeningProviderSimulator/Dockerfile -t ewp/screening-provider-simulator .

# ┌─ :: ARCHITECTURE + SECURITY :: Screening Provider simulator container image ────────────────────
# │ Why : Stands in for an external system in development, test and demo environments only - never
# │       deployed to production, where the real system is called.
# │ How : Same pattern as the APIs: SDK stage publishes, chiseled non-root ASP.NET runtime, plain
# │       HTTP on 8080. Its API key comes from the environment.
# │ Ref : doc/Containers.md | OWASP A05, A08 | CIS Docker Benchmark 4.1
# └─────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Simulators/ScreeningProviderSimulator/EnterpriseWebPlatform.Simulators.ScreeningProvider.csproj src/Simulators/ScreeningProviderSimulator/
RUN dotnet restore src/Simulators/ScreeningProviderSimulator/EnterpriseWebPlatform.Simulators.ScreeningProvider.csproj --runtime linux-x64

COPY src/Simulators/ScreeningProviderSimulator/ src/Simulators/ScreeningProviderSimulator/
RUN dotnet publish src/Simulators/ScreeningProviderSimulator/EnterpriseWebPlatform.Simulators.ScreeningProvider.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra AS runtime
WORKDIR /app

# Owned by root: the "app" user reads and runs the files but cannot change them.
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/*.
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.Simulators.ScreeningProvider.dll"]