using System.Collections.Specialized;
using System.Security.Claims;

using Duende.IdentityModel;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Validation;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;

using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Security;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ DPoP (RFC 9449) in the token exchange. A staff BFF exchanges its own sign-in token:
// │ the token must be bound to a key (cnf.jkt) and the BFF must prove THAT key. A Journey
// │ API exchanges the token its BFF sent it: that token must be bound (to the BFF) and the
// │ Journey API must prove a key of its own. A copied token - presented with another key,
// │ with no proof, or a token never bound - is refused.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class TokenExchangeGrantValidator_ValidateAsync_Edge_DPoPBinding
{
    private const string BffKey = "bff-key-thumbprint";
    private const string JourneyApiKey = "journey-api-key-thumbprint";

    private static readonly string[] ExchangeClients =
    [
        "AuditWeb", "AuditJourneyApi", "OperationsWeb", "OperationsJourneyApi", "PartnerProductsWeb",
        "PartnerProductsJourneyApi", "CustomerOnboardingWeb", "CustomerKycWeb", "SelfServiceWeb", "SelfServiceJourneyApi"
    ];

    // Every staff BFF that exchanges its own sign-in token (for Documents Management or its Journey API).
    private static readonly string[] Bffs = ["CustomerOnboardingWeb", "CustomerKycWeb", "AuditWeb", "OperationsWeb", "PartnerProductsWeb"];

    // Each staff Journey API, the BFF that calls it and the audience of the token it receives.
    private static readonly object[] JourneyApis =
    [
        new object[] { "AuditJourneyApi", "AuditWeb", "audit-journey-api" },
        new object[] { "OperationsJourneyApi", "OperationsWeb", "operations-journey-api" },
        new object[] { "PartnerProductsJourneyApi", "PartnerProductsWeb", "partner-products-journey-api" }
    ];

    [SetUp]
    public void Configure() =>
        ClientSettings.Initialize(new ConfigurationBuilder()
            .AddInMemoryCollection(ExchangeClients.ToDictionary(c => $"ClientIds:{c}", c => (string?)$"{c}.ClientID"))
            .Build());

    [TestCaseSource(nameof(Bffs))]
    public async Task Given_ATokenBoundToTheBffsKey_When_TheBffProvesThatKey_Then_TheExchangeIsGranted(string bff)
    {
        // Given
        var context = ExchangeBy($"{bff}.ClientID", proofKey: BffKey);
        var validator = ValidatorFor(SignInToken(bff, cnfJkt: BffKey));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeFalse();
        context.Result.Subject!.FindFirst(JwtClaimTypes.Subject)!.Value.Should().Be("agent-1");
    }

    [Test]
    public async Task Given_ATokenBoundToTheBffsKey_When_ItsKeyIsNotProved_Then_TheExchangeIsRefused(
        [ValueSource(nameof(Bffs))] string bff, [Values("another-key", null)] string? proofKey)
    {
        // Given
        var context = ExchangeBy($"{bff}.ClientID", proofKey);
        var validator = ValidatorFor(SignInToken(bff, cnfJkt: BffKey));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeTrue();
        context.Result.Error.Should().Be(OidcConstants.TokenErrors.InvalidGrant);
    }

    [TestCaseSource(nameof(Bffs))]
    public async Task Given_ABearerToken_When_TheBffExchangesItWithAProof_Then_TheExchangeIsRefused(string bff)
    {
        // Given - a token that was never bound (no cnf): it could be a copy from anywhere.
        var context = ExchangeBy($"{bff}.ClientID", proofKey: BffKey);
        var validator = ValidatorFor(SignInToken(bff, cnfJkt: null));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeTrue();
        context.Result.Error.Should().Be(OidcConstants.TokenErrors.InvalidGrant);
    }

    [TestCaseSource(nameof(JourneyApis))]
    public async Task Given_ATokenBoundToTheBffsKey_When_TheJourneyApiProvesItsOwnKey_Then_TheExchangeIsGranted(string journeyApi, string bff, string audience)
    {
        // Given - the token the BFF sent is bound to the BFF's key; the Journey API proves its own.
        var context = ExchangeBy($"{journeyApi}.ClientID", proofKey: JourneyApiKey);
        var validator = ValidatorFor(DelegatedToken(bff, audience, cnfJkt: BffKey));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeFalse();
        context.Result.Subject!.FindFirst(JwtClaimTypes.Subject)!.Value.Should().Be("agent-1");
    }

    [TestCaseSource(nameof(JourneyApis))]
    public async Task Given_ATokenBoundToTheBffsKey_When_TheJourneyApiProvesNoKey_Then_TheExchangeIsRefused(string journeyApi, string bff, string audience)
    {
        // Given - no proof: the new token could not be bound to the Journey API.
        var context = ExchangeBy($"{journeyApi}.ClientID", proofKey: null);
        var validator = ValidatorFor(DelegatedToken(bff, audience, cnfJkt: BffKey));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeTrue();
        context.Result.Error.Should().Be(OidcConstants.TokenErrors.InvalidGrant);
    }

    [TestCaseSource(nameof(JourneyApis))]
    public async Task Given_ABearerToken_When_TheJourneyApiExchangesItWithAProof_Then_TheExchangeIsRefused(string journeyApi, string bff, string audience)
    {
        // Given - a delegated token that was never bound (no cnf): it could be a copy from anywhere.
        var context = ExchangeBy($"{journeyApi}.ClientID", proofKey: JourneyApiKey);
        var validator = ValidatorFor(DelegatedToken(bff, audience, cnfJkt: null));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeTrue();
        context.Result.Error.Should().Be(OidcConstants.TokenErrors.InvalidGrant);
    }

    // The agent's own sign-in token from a staff web app.
    private static Claim[] SignInToken(string bff, string? cnfJkt) =>
        Bound(cnfJkt,
        [
            new(JwtClaimTypes.Subject, "agent-1"),
            new(JwtClaimTypes.ClientId, $"{bff}.ClientID"),
            new(JwtClaimTypes.AuthenticationMethod, "pwd")
        ]);

    // The token a staff BFF exchanged for its Journey API: the agent, with the BFF acting.
    private static Claim[] DelegatedToken(string bff, string audience, string? cnfJkt) =>
        Bound(cnfJkt,
        [
            new(JwtClaimTypes.Subject, "agent-1"),
            new(JwtClaimTypes.ClientId, $"{bff}.ClientID"),
            new(JwtClaimTypes.Audience, audience),
            new(JwtClaimTypes.Actor, $$"""{"client_id":"{{bff}}.ClientID"}""", "json"),
            new(JwtClaimTypes.AuthenticationMethod, "pwd")
        ]);

    private static Claim[] Bound(string? cnfJkt, List<Claim> claims)
    {
        if (cnfJkt is not null)
            claims.Add(new Claim(JwtClaimTypes.Confirmation, $$"""{"jkt":"{{cnfJkt}}"}""", "json"));
        return [.. claims];
    }

    private static ExtensionGrantValidationContext ExchangeBy(string clientId, string? proofKey) => new()
    {
        Request = new ValidatedTokenRequest
        {
            Client = new Client { ClientId = clientId },
            ProofKeyThumbprint = proofKey,
            Raw = new NameValueCollection
            {
                [OidcConstants.TokenRequest.SubjectToken] = "subject-token",
                [OidcConstants.TokenRequest.SubjectTokenType] = OidcConstants.TokenTypeIdentifiers.AccessToken
            }
        }
    };

    private static TokenExchangeGrantValidator ValidatorFor(Claim[] subjectTokenClaims) =>
        new(new FixedTokenValidator(subjectTokenClaims), NullLogger<TokenExchangeGrantValidator>.Instance);

    // The IDP's own check of the subject token (signature, lifetime) is not under test: it passes,
    // and returns the claims given.
    private sealed class FixedTokenValidator(Claim[] claims) : ITokenValidator
    {
        public Task<TokenValidationResult> ValidateAccessTokenAsync(string token, string? expectedScope = null, CancellationToken ct = default) =>
            Task.FromResult(new TokenValidationResult { IsError = false, Claims = claims });

        public Task<TokenValidationResult> ValidateIdentityTokenAsync(string token, string? clientId = null, bool validateLifetime = true, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}