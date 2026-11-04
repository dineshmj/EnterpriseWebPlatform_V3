using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

// ┌─ :: SECURITY :: Machine identity (OAuth 2.0 Client Credentials, private_key_jwt, DPoP) ────────
// │ Why : A worker that calls an API must prove WHICH service it is - never borrow a person's token
// │       or call anonymously; asking the IDP for a token per message would also overload it. A
// │       shared secret, or a token that works for whoever copies it, would let anyone who reads
// │       them act as the worker.
// │ How : The worker's own client (one per caller-callee purpose, narrowest scope) gets a token
// │       with Client Credentials, proving itself with a one-time assertion signed by its client
// │       key (no secret exists) and sending a DPoP proof, so the IDP binds the token to the
// │       worker's DPoP key. A token that comes back unbound is refused. It is cached until shortly
// │       before expiry, one refresh at a time, and dropped after a 401; every API call then
// │       carries a proof of its own (DPoPMachineTokenHandler).
// │ Ref : Features 2.1.2 | RFC 6749 section 4.4, RFC 7523, RFC 9449 | FAPI 2.0 | OAuth 2.1 |
// │       OWASP API2 Broken Authentication | Threat-Model R-9
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// OAuth 2.0 Client Credentials token for the subscriber's own machine identity, bound to its
/// DPoP key. The token is cached until shortly before it expires (instead of one token
/// request per message), and only one refresh runs at a time.
/// Uses the named HttpClient <see cref="HttpClientName"/>.
/// </summary>
public sealed class CachedM2MTokenClient(
    IHttpClientFactory httpClientFactory,
    IM2MClientSettings settings,
    MachineKeys keys,
    ILogger<CachedM2MTokenClient> logger)
{
    public const string HttpClientName = "IdentityServer";

    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;

    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    private Discovery? _idp;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // ┌─ PERF ───────────────────────────────────────────────────────────────────────────────
        // │ If the cached token is still valid, reuse it - one token request per token lifetime,
        // │ not one per message.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _accessToken;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
            // │ Checked again inside the lock: a caller that waited for the lock finds the token
            // │ another caller has just fetched, so only one token request goes to the IDP.
            // └──────────────────────────────────────────────────────────────────────────────────────
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _accessToken;

            var client = httpClientFactory.CreateClient(HttpClientName);
            var idp = _idp ??= await DiscoverAsync(client, cancellationToken);

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ If the IDP asks for a DPoP nonce (RFC 9449 section 8), send the request ONCE more
            // │ with it - new assertion, new proof; never loop.
            // └──────────────────────────────────────────────────────────────────────────────────────
            var (response, body) = await RequestTokenAsync(client, idp, nonce: null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest
                && body.Contains("use_dpop_nonce", StringComparison.Ordinal)
                && response.Headers.TryGetValues("DPoP-Nonce", out var nonces))
            {
                response.Dispose();
                (response, body) = await RequestTokenAsync(client, idp, nonces.First(), cancellationToken);
            }

            using (response)
            {
                // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                // │ If the IDP refuses the client, fail - the API is never called without a valid
                // │ token. The body names the OAuth error (e.g. invalid_client), never a token.
                // └──────────────────────────────────────────────────────────────────────────────────────
                if (!response.IsSuccessStatusCode)
                {
                    // Treated as transient by the caller: the message stays unprocessed.
                    throw new HttpRequestException(
                        $"IdentityServer token request failed with HTTP {(int)response.StatusCode}: {body}",
                        null,
                        response.StatusCode);
                }
            }

            var token = JsonSerializer.Deserialize<TokenResponse>(body);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new HttpRequestException("IdentityServer returned no access token.");

            // SEC: a bearer token here means the client is not set up for DPoP - refuse to use it.
            if (!string.Equals(token.TokenType, "DPoP", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"IdentityServer issued a '{token.TokenType}' token; this worker uses DPoP-bound tokens only.");

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) - RefreshSkew;

            logger.LogInformation(
                "M2M access token acquired for client {ClientId}, bound to DPoP key {Jkt}; cached until {ExpiresAt:u}.",
                settings.ClientId,
                keys.DPoPThumbprint,
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

    private async Task<(HttpResponseMessage Response, string Body)> RequestTokenAsync(
        HttpClient client, Discovery idp, string? nonce, CancellationToken cancellationToken)
    {
        var tokenEndpoint = new Uri(idp.TokenEndpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",      // RFC 6749 section 4.4, indicates it is an M2M token request
                ["client_id"] = settings.ClientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = keys.ClientAssertion(settings.ClientId, idp.Issuer),
                ["scope"] = settings.Scope
            })
        };
        request.Headers.Add("DPoP", keys.Proof(HttpMethod.Post, tokenEndpoint, nonce: nonce));

        // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
        // │ Exchange the signed assertion with the IDP for an access token that proves this
        // │ service's machine identity (M2M authentication), bound to its DPoP key.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var response = await client.SendAsync(request, cancellationToken);
        return (response, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The assertion's audience is the IDP's issuer EXACTLY as it states it (the IDP checks it
    // │ strictly), and the token endpoint is the one it names - read once from its discovery
    // │ document.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<Discovery> DiscoverAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/.well-known/openid-configuration", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"IdentityServer discovery failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);

        var discovery = await response.Content.ReadFromJsonAsync<Discovery>(cancellationToken);
        return discovery is { Issuer.Length: > 0, TokenEndpoint.Length: > 0 }
            ? discovery
            : throw new HttpRequestException("IdentityServer discovery named no issuer or token endpoint.");
    }

    private sealed record Discovery(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}