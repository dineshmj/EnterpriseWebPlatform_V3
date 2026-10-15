using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Validation;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.IdentityServer.Security;

// ┌─ :: SECURITY :: Token exchange (RFC 8693) with an explicit allow-list ─────────────────────────
// │ Why : A service calling another FOR A PERSON must keep that person as the subject - and no
// │       service may turn just any token into a token for any other service.
// │ How : Who may exchange what is an allow-list (fail closed); a token without a person (sub) is
// │       never exchanged; the requester is added to the nested act claim; the person's original
// │       auth_time and amr are kept, so step-up holds at every hop.
// │ Ref : Features 2.1.2 | RFC 8693 OAuth 2.0 Token Exchange | OWASP API2 / API5 |
// │       Authorization-Model 9.3
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// OAuth 2.0 Token Exchange (RFC 8693), delegation style: a service that received a person's
/// token swaps it for a new one aimed at the next service. The new token keeps the PERSON as
/// its subject (sub, and so their roles, permissions and branch, re-read by the profile service)
/// and names the acting service in the "act" claim - nested when it was already delegated:
///
///   { "sub": "&lt;sarah&gt;", "act": { "client_id": "Audit.JourneyApi.ClientID",
///                               "act": { "client_id": "Audit.Microservice.Web.ClientID" } } }
///
/// So a Domain API can enforce role, branch and separation of duties against the real person,
/// AND know exactly which chain of services is calling. Which client may exchange which token
/// is an explicit allow-list (fail closed); a token without a person is never exchanged, and a
/// deactivated person's token stops working along the whole chain (IsActive).
/// </summary>
public sealed class TokenExchangeGrantValidator(
    ITokenValidator tokenValidator,
    ILogger<TokenExchangeGrantValidator> logger) : IExtensionGrantValidator
{
    public string GrantType => OidcConstants.GrantTypes.TokenExchange;

    /// <summary>
    /// Who may exchange what. A BFF (Audit web, Customer Onboarding, KYC): only a person's token
    /// it obtained itself at sign-in (not yet delegated). The Audit Journey API: only a token the
    /// Audit web BFF exchanged for the Journey API. The scopes a client may ask for are its
    /// AllowedScopes (e.g. the KYC BFF: documents-management.read only).
    /// </summary>
    private static readonly Dictionary<string, Func<SubjectToken, bool>> AllowedExchanges = new()
    {
        [AuditMicroservice.CLIENT_ID_FOR_IDP] = token =>
            token.ClientId == AuditMicroservice.CLIENT_ID_FOR_IDP && token.ActingClientId is null,

        // The Operations web BFF: only the person's own token from its sign-in, for its Journey API.
        [OperationsMicroservice.CLIENT_ID_FOR_IDP] = token =>
            token.ClientId == OperationsMicroservice.CLIENT_ID_FOR_IDP && token.ActingClientId is null,

        [AuditMicroservice.CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API] = token =>
            token.Audiences.Contains(MicroserviceApiResourceNames.AUDIT_JOURNEY_API) &&
            token.ActingClientId == AuditMicroservice.CLIENT_ID_FOR_IDP,

        // The Operations Journey API: only a token the Operations web BFF exchanged for it (parked
        // Outbox messages of Accounts and Compliance; the scopes are its AllowedScopes).
        [OperationsMicroservice.CLIENT_ID_FOR_IDP_FOR_OPERATIONS_JOURNEY_API] = token =>
            token.Audiences.Contains(MicroserviceApiResourceNames.OPERATIONS_JOURNEY_API) &&
            token.ActingClientId == OperationsMicroservice.CLIENT_ID_FOR_IDP,

        // Documents Management for the signed-in agent (upload, clean-up of an unfinished submission).
        [CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP] = token =>
            token.ClientId == CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP && token.ActingClientId is null,

        // Documents Management for the signed-in KYC officer (reading the evidence).
        [CustomerKycMicroservice.CLIENT_ID_FOR_IDP] = token =>
            token.ClientId == CustomerKycMicroservice.CLIENT_ID_FOR_IDP && token.ActingClientId is null
    };

    public async Task ValidateAsync(ExtensionGrantValidationContext context, CancellationToken cancellationToken)
    {
        var requester = context.Request.Client.ClientId;
        var raw = context.Request.Raw;
        var subjectToken = raw.Get(OidcConstants.TokenRequest.SubjectToken);

        if (string.IsNullOrWhiteSpace(subjectToken) ||
            raw.Get(OidcConstants.TokenRequest.SubjectTokenType) != OidcConstants.TokenTypeIdentifiers.AccessToken)
        {
            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidRequest,
                "subject_token (an access token, subject_token_type urn:ietf:params:oauth:token-type:access_token) is required.");
            return;
        }

        var validation = await tokenValidator.ValidateAccessTokenAsync(subjectToken, expectedScope: null, cancellationToken);
        if (validation.IsError)
        {
            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant, "The subject token is not valid.");
            return;
        }

        var token = SubjectToken.From(validation.Claims ?? []);
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the subject token has no person (a machine token), refuse: delegation always acts
        // │ for a person.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (token.Subject is null)
        {
            // A machine's own token has no person to act for: delegation needs one.
            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant, "The subject token has no person (sub).");
            return;
        }

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If this client is not allowed to exchange this kind of token, refuse (fail closed)
        // │ and log it.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!AllowedExchanges.TryGetValue(requester, out var allowed) || !allowed(token))
        {
            logger.LogWarning("Token exchange refused: client {Requester} may not exchange a token of client {Client} (acting: {Acting}).",
                requester, token.ClientId, token.ActingClientId ?? "-");
            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant, "This client may not exchange this token.");
            return;
        }

        // act = this client, wrapping the chain the subject token already carried.
        var actor = new JsonObject { ["client_id"] = requester };
        if (token.Actor is not null)
            actor["act"] = JsonNode.Parse(token.Actor);

        // The delegated token acts for the person's ORIGINAL sign-in: it keeps that sign-in's time
        // (auth_time) and methods (amr: pwd / otp / mfa) next to the exchange itself, so a
        // downstream API can still require a recent MFA sign-in (step-up) - an exchange is never
        // a new authentication of the person.
        List<Claim> claims = [new Claim(JwtClaimTypes.Actor, actor.ToJsonString(), IdentityServerConstants.ClaimValueTypes.Json)];
        claims.AddRange(token.AuthenticationMethods
            .Where(m => m != GrantType)
            .Select(m => new Claim(JwtClaimTypes.AuthenticationMethod, m)));

        context.Result = token.AuthTime is { } authTime
            ? new GrantValidationResult(subject: token.Subject, authenticationMethod: GrantType, authTime: authTime.UtcDateTime, claims: claims)
            : new GrantValidationResult(subject: token.Subject, authenticationMethod: GrantType, claims: claims);

        logger.LogInformation("Token exchange: {Requester} now acts for subject {Subject} (chain {Actor}).", requester, token.Subject, actor.ToJsonString());
    }

    /// <summary>The parts of the presented token the rules look at.</summary>
    private sealed record SubjectToken(
        string? Subject, string? ClientId, IReadOnlySet<string> Audiences, string? Actor, string? ActingClientId,
        IReadOnlyList<string> AuthenticationMethods, DateTimeOffset? AuthTime)
    {
        public static SubjectToken From(IEnumerable<Claim> claims)
        {
            var list = claims.ToList();
            var actor = list.FirstOrDefault(c => c.Type == JwtClaimTypes.Actor)?.Value;
            string? actingClient = null;
            if (actor is not null)
            {
                try { actingClient = JsonNode.Parse(actor)?["client_id"]?.GetValue<string>(); }
                catch (JsonException) { actingClient = null; }
            }

            return new SubjectToken(
                list.FirstOrDefault(c => c.Type == JwtClaimTypes.Subject)?.Value,
                list.FirstOrDefault(c => c.Type == JwtClaimTypes.ClientId)?.Value,
                list.Where(c => c.Type == JwtClaimTypes.Audience).Select(c => c.Value).ToHashSet(),
                actor,
                actingClient,
                list.Where(c => c.Type == JwtClaimTypes.AuthenticationMethod).Select(c => c.Value).Distinct().ToList(),
                long.TryParse(list.FirstOrDefault(c => c.Type == JwtClaimTypes.AuthenticationTime)?.Value, out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null);
        }
    }
}