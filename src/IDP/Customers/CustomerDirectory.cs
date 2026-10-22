using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;

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
// │       customer-onboarding.online-identity, audience customer-onboarding-api. The API pins both.
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
            Claims =
            {
                new Claim(JwtClaimTypes.ClientId, clientIds.IdpCustomerDirectory),
                new Claim(JwtClaimTypes.Scope, Scope),
                new Claim(JwtClaimTypes.JwtId, Guid.NewGuid().ToString("N"))
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.CreateSecurityTokenAsync(token, cancellationToken));
        return request;
    }
}

/// <summary>Customer Onboarding did not answer: the registration cannot go on now.</summary>
public sealed class CustomerDirectoryUnavailableException : Exception;