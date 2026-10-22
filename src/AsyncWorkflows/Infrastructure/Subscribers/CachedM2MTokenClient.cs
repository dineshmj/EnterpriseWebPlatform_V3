using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

// ┌─ :: SECURITY :: Machine identity (OAuth 2.0 Client Credentials) ───────────────────────────────
// │ Why : A worker that calls an API must prove WHICH service it is - never borrow a person's token
// │       or call anonymously; asking the IDP for a token per message would also overload it.
// │ How : The worker's own client (one per caller-callee purpose, narrowest scope) gets a token
// │       with Client Credentials; it is cached until shortly before expiry, one refresh at a time,
// │       and dropped after a 401.
// │ Ref : Features 2.1.2 | RFC 6749 section 4.4 | OAuth 2.1 | OWASP API2 Broken Authentication
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// OAuth 2.0 Client Credentials token for the subscriber's own machine identity.
/// The token is cached until shortly before it expires (instead of one token
/// request per message), and only one refresh runs at a time.
/// Uses the named HttpClient <see cref="HttpClientName"/>.
/// </summary>
public sealed class CachedM2MTokenClient(
    IHttpClientFactory httpClientFactory,
    IM2MClientSettings settings,
    ILogger<CachedM2MTokenClient> logger)
{
    public const string HttpClientName = "IdentityServer";

    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;

    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ If the cached token is still valid, reuse it - one token request per token lifetime,
        // │ not one per message.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _accessToken;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _accessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",      // RFC 6749 section 4.4, indicates it is an M2M token request
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret,
                    ["scope"] = settings.Scope
                })
            };

            // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
            // │ Exchange client credentials with the IDP for an access token that proves this
            // │ service's machine identity (M2M authentication).
            // └──────────────────────────────────────────────────────────────────────────────────────
            using var response = await httpClientFactory
                .CreateClient(HttpClientName)
                .SendAsync(request, cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ If the IDP refuses the client credentials, fail - the API is never called without a
            // │ valid token.
            // └──────────────────────────────────────────────────────────────────────────────────────
            if (!response.IsSuccessStatusCode)
            {
                // Treated as transient by the caller: the message stays unprocessed.
                throw new HttpRequestException(
                    $"IdentityServer token request failed with HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            var token = JsonSerializer.Deserialize<TokenResponse>(body);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new HttpRequestException("IdentityServer returned no access token.");

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) - RefreshSkew;

            logger.LogInformation(
                "M2M access token acquired for client {ClientId}; cached until {ExpiresAt:u}.",
                settings.ClientId,
                _expiresAt);

            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Forces a new token on the next call (e.g. after a 401).</summary>
    public void Invalidate() => _accessToken = null;

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}