using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Configuration;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Authentication;

/// <summary>
/// OAuth 2.0 Client Credentials token for the subscriber's own machine identity.
/// The token is cached until shortly before it expires (instead of one token
/// request per message), and only one refresh runs at a time.
/// </summary>
public sealed class CachedM2MTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptions<KycSubscriberOptions> options,
    ILogger<CachedM2MTokenClient> logger)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly KycSubscriberOptions _options = options.Value;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;

    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
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
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["scope"] = _options.Scope
                })
            };

            using var response = await httpClientFactory
                .CreateClient("IdentityServer")
                .SendAsync(request, cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Treated as transient by the caller: the message stays unprocessed.
                throw new HttpRequestException(
                    $"IdentityServer token request failed with HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            var token = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
                ?? throw new HttpRequestException("IdentityServer returned an empty token response.");

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) - RefreshSkew;

            logger.LogInformation(
                "M2M access token acquired for client {ClientId}; cached until {ExpiresAt:u}.",
                _options.ClientId,
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
