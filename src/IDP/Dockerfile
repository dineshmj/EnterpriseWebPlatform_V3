# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/IDP/Dockerfile -t ewp/idp .

# ┌─ :: ARCHITECTURE + SECURITY :: Identity provider container image ──────────────────────────────
# │ Why : Whoever holds the IDP's signing key can mint any token for any API. The image is
# │       published to a registry, so no key, client secret or password may be in it - not even
# │       the development ones.
# │ How : The .NET SDK publishes the IDP; a chiseled, non-root ASP.NET runtime runs it on plain
# │       HTTP 8080 (TLS ends at the ingress; the issuer is https from X-Forwarded-Proto). At run
# │       time, from the environment or mounted secret files only:
# │         SigningCredential__CertificatePath / __CertificatePassword - the token-signing PFX
# │           (outside Development the IDP refuses to start without it; no developer key),
# │         ClientKeys__<client id> - path of each private_key_jwt client's PUBLIC JWK,
# │         ClientSecrets__<client id> - each confidential client's secret,
# │         ConnectionStrings__IdentityDbConnection - users, sessions, keys, PAR requests.
# │       .dockerignore keeps tempkey*.jwk, keys/ and appsettings.Development.json out.
# │ Ref : doc/Containers.md | Features 1.6.5, 2.4.5 | OWASP A02, A05, A08 | Threat-Model R-10
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/Common/WebUtilities/EnterpriseWebPlatform.Common.WebUtilties.csproj src/Common/WebUtilities/
COPY src/Common/Observability/EnterpriseWebPlatform.Common.Observability.csproj src/Common/Observability/
COPY src/IDP/EnterpriseWebPlatform.IdentityServer.csproj src/IDP/
RUN dotnet restore src/IDP/EnterpriseWebPlatform.IdentityServer.csproj --runtime linux-x64

COPY src/Common/WebUtilities/ src/Common/WebUtilities/
COPY src/Common/Observability/ src/Common/Observability/
# The design tokens and the mark the sign-in pages use (CopyDesignSystemAssets copies them into wwwroot).
COPY src/Common/DesignSystem/ src/Common/DesignSystem/
COPY src/IDP/ src/IDP/
RUN dotnet publish src/IDP/EnterpriseWebPlatform.IdentityServer.csproj \
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
ENTRYPOINT ["dotnet", "EnterpriseWebPlatform.IdentityServer.dll"]