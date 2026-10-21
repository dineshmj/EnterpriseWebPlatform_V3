using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

public sealed class MfeCustomerKyc
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // CustomerKyc Microservice Client (BFF using NestJS, and not ASP.NET Core 10).
                new()
                {
                    ClientId = ClientSettings.Ids.CustomerKycWeb,
                    ClientName = "Customer KYC Microservice BFF Client",
                    ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.CustomerKycWeb) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for the BFF, plus token exchange (RFC 8693) for Documents
                    // │ Management (read - the officer views the evidence), always acting for the signed-in
                    // │ person.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },
                    RequirePkce = true,

                    RedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerKycWeb))}/api/auth/callback" },
                    PostLogoutRedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerKycWeb))}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerKycWeb))}/signout-oidc",
                    // Back-channel logout (server-to-server; NestJS BackChannelLogoutController):
                    // ends the BFF session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.CustomerKycWeb))}/backchannel-logout",
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
                        "customer-kyc-api",
                        "customer-kyc.read",
                        "customer-kyc.write",
                        // Requested only in the token exchange, never at sign-in.
                        "documents-management.read"
                
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