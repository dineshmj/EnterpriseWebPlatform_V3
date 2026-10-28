using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;

// ┌─ :: ARCHITECTURE + SECURITY :: Grafana signs in through EWP's IDP ─────────────────────────────
// │ Why : The cloud demo's Grafana (deploy/helm/ewp-observability) is public; a second login of its
// │       own would be one more password outside the IDP's MFA, lockout and audit.
// │ How : A confidential client (Authorization Code + PKCE) asking for the person's roles only;
// │       Grafana admits operations_administrator (Viewer) and platform_administrator (Admin) and
// │       refuses everyone else (role_attribute_strict). Registered ONLY where Grafana exists:
// │       when ClientBaseUrls:Grafana is set (the cloud demo with observability on) - a laptop
// │       and an environment without Grafana have no such client at all.
// │ Ref : doc/Cloud-Demo.md (observability) | deploy/helm/ewp-observability | OWASP A07
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The cloud demo's Grafana: signs operations staff in, nothing else (no API scope, no token exchange).</summary>
public sealed class ObservabilityGrafana
    : IDuendeClient
{
    /// <summary>The key under ClientIds / ClientBaseUrls.</summary>
    public const string Key = "Grafana";

    /// <summary>Whether this environment has a Grafana to register (ClientBaseUrls:Grafana set).</summary>
    public static bool IsConfigured => ClientSettings.HasBaseUrl(Key);

    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.Grafana,
            ClientName = "Grafana (cloud demo observability)",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.Grafana) },

            AllowedGrantTypes = GrantTypes.Code,
            RequirePkce = true,

            // Grafana's generic OAuth callback; its sign-out returns to its own login page.
            RedirectUris = { $"{ClientSettings.BaseUrlOf(Key)}/login/generic_oauth" },
            PostLogoutRedirectUris = { $"{ClientSettings.BaseUrlOf(Key)}/login" },

            // SEC: no refresh token - a Grafana session ends with its own cookie; sign in again.
            AllowOfflineAccess = false,
            // Grafana reads the roles from the ID token (role_attribute_path).
            AlwaysIncludeUserClaimsInIdToken = true,

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                "roles"
            }
        };
}