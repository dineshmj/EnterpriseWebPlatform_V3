using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Services;

public interface IM2MAccessTokenService
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class M2MAccessTokenService : IM2MAccessTokenService
{
    private const string CacheKey = "customer-onboarding-bff-to-dm-m2m-access-token";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly CustomerOnboardingBffOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<M2MAccessTokenService> _logger;

    public M2MAccessTokenService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<CustomerOnboardingBffOptions> options,
        ILogger<M2MAccessTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey, out string? cachedToken) && !string.IsNullOrWhiteSpace(cachedToken))
        {
            return cachedToken;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(CacheKey, out cachedToken) && !string.IsNullOrWhiteSpace(cachedToken))
            {
                return cachedToken;
            }

            return await RequestTokenWithRetryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> RequestTokenWithRetryAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("IdentityServerTokenClient");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = _options.M2MClientId,
                        ["client_secret"] = _options.M2MClientSecret,
                        ["scope"] = "documents-management.write"
                    })
                };

                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < maxAttempts)
                    {
                        _logger.LogWarning(
                            "Transient M2M token endpoint failure ({StatusCode}) on attempt {Attempt}/{MaxAttempts}. Retrying.",
                            (int)response.StatusCode,
                            attempt,
                            maxAttempts);
                        await Task.Delay(delay, cancellationToken);
                        delay *= 2;
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Documents Management M2M token acquisition failed with HTTP {(int)response.StatusCode}: {body}");
                }

                var token = JsonSerializer.Deserialize<TokenResponse>(body)
                    ?? throw new InvalidOperationException("IdentityServer returned an empty token response.");

                if (string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    throw new InvalidOperationException("IdentityServer did not return an access_token.");
                }

                var cacheLifetimeSeconds = Math.Max(
                    30,
                    token.ExpiresIn - _options.M2MTokenRefreshSkewSeconds);

                _cache.Set(
                    CacheKey,
                    token.AccessToken,
                    TimeSpan.FromSeconds(cacheLifetimeSeconds));

                _logger.LogDebug(
                    "Obtained and cached Customer Onboarding BFF -> Documents Management M2M access token for approximately {LifetimeSeconds} seconds.",
                    cacheLifetimeSeconds);

                return token.AccessToken;
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "Transient network failure while obtaining M2M token on attempt {Attempt}/{MaxAttempts}. Retrying.",
                    attempt,
                    maxAttempts);
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }

        throw new InvalidOperationException("M2M token acquisition failed after the configured retry attempts.");
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
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);
}