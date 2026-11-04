using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Duende.IdentityModel;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;

namespace EnterpriseWebPlatform.IdentityServer.Customers;

/// <summary>What Customer Onboarding tells the IDP about a customer who registers.</summary>
public sealed record RegistrationCandidate(string CustomerNumber, string DisplayName, string MobileEnding);

public enum LinkResult
{
    Linked,
    NoMatch,
    AlreadyRegistered,
    Unavailable
}

// ┌─ :: ARCHITECTURE + SECURITY :: The IDP asks Customer Onboarding, as itself ────────────────────
// │ Why : Customer Onboarding owns who EWP's customers are; the IDP must not copy that data, and
// │       its question must reach exactly the two internal endpoints made for it.
// │ How : The IDP signs a 60-second access token in-process with its own token service: client_id
// │       IdentityServer.CustomerDirectory.ClientID (a pseudo-client - not registered, no secret
// │       anywhere, nobody can request a token for it), the single scope
// │       customer-onboarding.online-identity, audience customer-onboarding-api, bound to a DPoP key
// │       only this process holds (ProofKey below). The API pins client and scope.
// │ Ref : Features 2.1.6 | Authorization-Model 9 | RFC 9068 | OWASP API5
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Customer Onboarding's internal online-identity endpoints, called by the IDP.</summary>
public sealed class CustomerDirectory(
    HttpClient http,
    ITokenService tokens,
    IIssuerNameService issuer,
    ClientIds clientIds,
    TimeProvider time,
    ILogger<CustomerDirectory> logger)
{
    private const string Audience = "customer-onboarding-api";
    private const string Scope = "customer-onboarding.online-identity";

    /// <summary>The customer these details belong to, or null (no match - or Customer Onboarding unreachable).</summary>
    public async Task<RegistrationCandidate?> FindCandidateAsync(string customerNumber, string email, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await RequestAsync("internal/v1/customers/online-identity/candidates", new { customerNumber, email }, cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<RegistrationCandidate>(cancellationToken);
        }
        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ If Customer Onboarding does not answer (down, or the 10-second timeout), the
        // │ registration stops with "try again later" - never a half-linked sign-in.
        // └──────────────────────────────────────────────────────────────────────────────────────
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Customer Onboarding could not be asked about an online registration.");
            throw new CustomerDirectoryUnavailableException();
        }
    }

    /// <summary>Links the new sign-in's subject to the customer.</summary>
    public async Task<LinkResult> LinkAsync(string customerNumber, string email, Guid subjectId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await RequestAsync("internal/v1/customers/online-identity/links", new { customerNumber, email, subjectId }, cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent or HttpStatusCode.OK => LinkResult.Linked,
                HttpStatusCode.NotFound => LinkResult.NoMatch,
                HttpStatusCode.Conflict => LinkResult.AlreadyRegistered,
                _ => LinkResult.Unavailable
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Customer Onboarding could not link an online identity.");
            return LinkResult.Unavailable;
        }
    }

    private async Task<HttpRequestMessage> RequestAsync(string path, object body, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var token = new Token(OidcConstants.TokenTypes.AccessToken)
        {
            CreationTime = now.UtcDateTime,
            Issuer = await issuer.GetCurrentAsync(cancellationToken),
            Lifetime = 60,
            AccessTokenType = AccessTokenType.Jwt,
            Audiences = { Audience },
            // SEC: bound to the IDP's own proof key (cnf.jkt) - the API refuses every unbound token.
            Confirmation = JsonSerializer.Serialize(new { jkt = ProofKey.Thumbprint }),
            Claims =
            {
                new Claim(JwtClaimTypes.ClientId, clientIds.IdpCustomerDirectory),
                new Claim(JwtClaimTypes.Scope, Scope),
                new Claim(JwtClaimTypes.JwtId, Guid.NewGuid().ToString("N"))
            }
        };
        var accessToken = await tokens.CreateSecurityTokenAsync(token, cancellationToken);

        var address = new Uri(http.BaseAddress!, path);
        var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
        request.Headers.Add("DPoP", ProofKey.Proof(HttpMethod.Post, address, accessToken, now));
        return request;
    }

    // ┌─ :: SECURITY :: DPoP for the IDP's own token ──────────────────────────────────────────────────
    // │ Why : Every Domain API refuses a token that is not bound to a key (FAPI 2.0 sender-constrained
    // │       tokens) - a token the IDP mints for itself is no exception: copied from a log or a proxy
    // │       within its 60 s, it must be useless.
    // │ How : An EC P-256 key made in memory at start-up and never written anywhere (the token is made
    // │       and spent in this same process, so no one else ever needs it; a restart simply makes a
    // │       new one). The token names it (cnf.jkt, RFC 7638 thumbprint); each call carries a new
    // │       proof for exactly its method and address, with the token's hash (ath).
    // │ Ref : Features 2.1.2 | RFC 9449, 7638 | FAPI 2.0 Security Profile §5.3.1 | Threat-Model R-9
    // └────────────────────────────────────────────────────────────────────────────────────────────────
    private static class ProofKey
    {
        private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        private static readonly ECPoint Point = Key.ExportParameters(includePrivateParameters: false).Q;

        // RFC 7638: SHA-256 over the required members, in lexical order, without spaces.
        public static readonly string Thumbprint = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.EncodeToString(Point.X)}\",\"y\":\"{Base64Url.EncodeToString(Point.Y)}\"}}")));

        public static string Proof(HttpMethod method, Uri address, string accessToken, DateTimeOffset now)
        {
            var header = new { typ = "dpop+jwt", alg = "ES256", jwk = new { kty = "EC", crv = "P-256", x = Base64Url.EncodeToString(Point.X), y = Base64Url.EncodeToString(Point.Y) } };
            var claims = new
            {
                jti = Guid.NewGuid().ToString("N"),
                htm = method.Method,
                htu = address.GetLeftPart(UriPartial.Path),
                iat = now.ToUnixTimeSeconds(),
                ath = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)))
            };
            var unsigned = $"{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims))}";
            // .NET signs in the IEEE P1363 form JWS asks for.
            return $"{unsigned}.{Base64Url.EncodeToString(Key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256))}";
        }
    }
}

/// <summary>Customer Onboarding did not answer: the registration cannot go on now.</summary>
public sealed class CustomerDirectoryUnavailableException : Exception;