using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

public sealed class MfeCustomerOnboarding
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Customer Onboarding Microservice Client (BFF using ASP.NET Core 10)
                new()
                {
                    ClientId = ClientSettings.Ids.CustomerOnboardingWeb,
                    ClientName = "Customer Onboarding Microservice BFF Client",
                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ private_key_jwt (RFC 7523, FAPI 2.0): the BFF signs a one-minute assertion with
                    // │ its own EC P-256 key (ES256); the IDP holds only the PUBLIC key - no shared secret
                    // │ to leak, or to rotate on both sides.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    ClientSecrets = { ClientKeyStore.For(ClientSettings.Ids.CustomerOnboardingWeb) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for the BFF, plus token exchange (RFC 8693) for Documents
                    // │ Management (write - the agent uploads the evidence), always acting for the signed-in
                    // │ person.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },

                    RequirePkce = true,

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ PAR (RFC 9126, FAPI 2.0): the BFF pushes the authorization request to the IDP over the
                    // │ back channel, authenticated; the browser carries only a one-time request_uri (60 s), so
                    // │ no one can read or change the request on its way (scopes, redirect, PKCE challenge).
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    RequirePushedAuthorization = true,
                    PushedAuthorizationLifetime = 60,

                    RedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerOnboardingWeb))}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerOnboardingWeb))}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerOnboardingWeb))}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerOnboardingWeb))}/bff/backchannel",
                    BackChannelLogoutSessionRequired = true,

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Refresh tokens for the BFF (offline_access), so the session is renewed without a new
                    // │ sign-in.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowOfflineAccess = true,


                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles",
                        "organization",
                        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                        // │ API scopes: only what this BFF's screens need; the Documents Management scope is
                        // │ requested only in the token exchange, never at sign-in.
                        // └──────────────────────────────────────────────────────────────────────────────────────
                        "customer-onboarding-api",
                        "customer-onboarding.read",
                        "customer-onboarding.write",
                        // Requested only in the token exchange, never at sign-in.
                        "documents-management.write"
                    },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Each refresh re-reads the CURRENT roles and ABAC attributes (branch, clearance), so
                    // │ an administrative change takes effect within one access-token lifetime.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    UpdateAccessTokenClaimsOnRefresh = true,

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Refresh-token rotation (OAuth 2.1 / RFC 9700): every refresh returns a new token and
                    // │ invalidates the old one - a stolen refresh token dies at its next legitimate use.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    RefreshTokenUsage = TokenUsage.OneTimeOnly,

                    RefreshTokenExpiration = TokenExpiration.Sliding,
                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Sliding refresh lifetime (one hour per use): active people stay signed in, idle
                    // │ sessions expire.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    SlidingRefreshTokenLifetime = 3600
                };
        }
    }
}