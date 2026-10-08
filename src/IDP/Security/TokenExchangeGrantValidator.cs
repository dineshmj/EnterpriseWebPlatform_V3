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
    /// Who may exchange what. The Audit web BFF: only a person's token it obtained itself at
    /// sign-in (not yet delegated). The Audit Journey API: only a token the Audit web BFF
    /// exchanged for the Journey API.
    /// </summary>
    private static readonly Dictionary<string, Func<SubjectToken, bool>> AllowedExchanges = new()
    {
        [AuditMicroservice.CLIENT_ID_FOR_IDP] = token =>
            token.ClientId == AuditMicroservice.CLIENT_ID_FOR_IDP && token.ActingClientId is null,

        [AuditMicroservice.CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API] = token =>
            token.Audiences.Contains(MicroserviceApiResourceNames.AUDIT_JOURNEY_API) &&
            token.ActingClientId == AuditMicroservice.CLIENT_ID_FOR_IDP
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

        var token = SubjectToken.From(validation.Claims);
        if (token.Subject is null)
        {
            // A machine's own token has no person to act for: delegation needs one.
            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant, "The subject token has no person (sub).");
            return;
        }

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

        context.Result = new GrantValidationResult(
            subject: token.Subject,
            authenticationMethod: GrantType,
            claims: [new Claim(JwtClaimTypes.Actor, actor.ToJsonString(), IdentityServerConstants.ClaimValueTypes.Json)]);

        logger.LogInformation("Token exchange: {Requester} now acts for subject {Subject} (chain {Actor}).", requester, token.Subject, actor.ToJsonString());
    }

    /// <summary>The parts of the presented token the rules look at.</summary>
    private sealed record SubjectToken(string? Subject, string? ClientId, IReadOnlySet<string> Audiences, string? Actor, string? ActingClientId)
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
                actingClient);
        }
    }
}