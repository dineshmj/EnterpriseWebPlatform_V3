using System.Text.Json;
using System.Text.Json.Serialization;

using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Services;

public interface IDocumentsManagementTokenService
{
    /// <summary>A Documents Management token for the signed-in agent (token exchange).</summary>
    Task<string> GetTokenAsync(HttpContext httpContext, CancellationToken cancellationToken);
}

/// <summary>
/// OAuth 2.0 Token Exchange (RFC 8693): swaps the signed-in agent's access token for a
/// short-lived Documents Management token. The agent stays the subject (so Documents Management
/// reads THEIR branch from the token, issued by the IDP), and this BFF is named as the acting
/// client ("act"). Nothing is cached across people: one exchange per request that needs it.
/// </summary>
public sealed class DocumentsManagementTokenService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<DocumentsManagementTokenService> logger) : IDocumentsManagementTokenService
{
    private const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    // Required at start-up by the OIDC set-up in Program.cs.
    private readonly string _clientSecret = configuration["Oidc:ClientSecret"]
        ?? throw new InvalidOperationException("Oidc:ClientSecret is not configured.");

    public async Task<string> GetTokenAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        // The agent's current access token (refreshed by Duende when close to expiry).
        UserToken userToken = await httpContext.GetUserAccessTokenAsync(ct: cancellationToken).GetToken();

        const int maxAttempts = 3;
        var delay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var client = httpClientFactory.CreateClient("IdentityServerTokenClient");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = TokenExchangeGrantType,
                        ["client_id"] = CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP,
                        ["client_secret"] = _clientSecret,
                        ["subject_token"] = userToken.AccessToken,
                        ["subject_token_type"] = AccessTokenType,
                        ["scope"] = DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE
                    })
                };

                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < maxAttempts)
                    {
                        logger.LogWarning(
                            "Transient token endpoint failure ({StatusCode}) on attempt {Attempt}/{MaxAttempts}. Retrying.",
                            (int)response.StatusCode, attempt, maxAttempts);
                        await Task.Delay(delay, cancellationToken);
                        delay *= 2;
                        continue;
                    }

                    // The body names the OAuth error (e.g. invalid_grant), never a token.
                    throw new InvalidOperationException(
                        $"The Documents Management token exchange failed with HTTP {(int)response.StatusCode}: {body}");
                }

                var token = JsonSerializer.Deserialize<TokenResponse>(body);
                return string.IsNullOrWhiteSpace(token?.AccessToken)
                    ? throw new InvalidOperationException("IdentityServer did not return an access_token.")
                    : token.AccessToken;
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                logger.LogWarning(
                    "Transient network failure during the token exchange on attempt {Attempt}/{MaxAttempts}. Retrying.",
                    attempt, maxAttempts);
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
        => statusCode is
            System.Net.HttpStatusCode.RequestTimeout or
            System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.BadGateway or
            System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout or
            System.Net.HttpStatusCode.InternalServerError;

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken);
}