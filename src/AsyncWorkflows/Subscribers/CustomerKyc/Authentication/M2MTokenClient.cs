using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Authentication;

public sealed class M2MTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptions<CustomerKycSubscriberOptions> options,
    ILogger<M2MTokenClient> logger)
{
    private readonly CustomerKycSubscriberOptions _options = options.Value;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // DEBUG POINT #2: Put a breakpoint on the next line to inspect M2M token acquisition.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/connect/token")
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
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "M2M token request failed. StatusCode={StatusCode}, Response={Response}",
                (int)response.StatusCode,
                body);

            throw new InvalidOperationException(
                $"IdentityServer token request failed with HTTP {(int)response.StatusCode}.");
        }

        var token = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException("IdentityServer returned an empty token response.");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException("IdentityServer token response did not contain access_token.");

        logger.LogInformation(
            "M2M access token acquired for client {ClientId} and scope {Scope}.",
            _options.ClientId,
            _options.Scope);

        return token.AccessToken;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}