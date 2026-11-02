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
// │ DPoP (RFC 9449) in the token exchange for a staff BFF that exchanges its own sign-in
// │ token (Customer Onboarding, for Documents Management): the subject token must be bound
// │ to a key (cnf.jkt) and the BFF must prove THAT key on the exchange. A copied token -
// │ presented with another key, with no proof, or a token never bound - is refused.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class TokenExchangeGrantValidator_ValidateAsync_Edge_DPoPBinding
{
    private const string BffKey = "bff-key-thumbprint";

    private static readonly string[] ExchangeClients =
    [
        "AuditWeb", "AuditJourneyApi", "OperationsWeb", "OperationsJourneyApi", "PartnerProductsWeb",
        "PartnerProductsJourneyApi", "CustomerOnboardingWeb", "CustomerKycWeb", "SelfServiceWeb", "SelfServiceJourneyApi"
    ];

    [SetUp]
    public void Configure() =>
        ClientSettings.Initialize(new ConfigurationBuilder()
            .AddInMemoryCollection(ExchangeClients.ToDictionary(c => $"ClientIds:{c}", c => (string?)$"{c}.ClientID"))
            .Build());

    [Test]
    public async Task Given_ATokenBoundToTheBffsKey_When_TheBffProvesThatKey_Then_TheExchangeIsGranted()
    {
        // Given
        var context = ExchangeBy("CustomerOnboardingWeb.ClientID", proofKey: BffKey);
        var validator = ValidatorFor(SignInToken(cnfJkt: BffKey));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeFalse();
        context.Result.Subject!.FindFirst(JwtClaimTypes.Subject)!.Value.Should().Be("agent-1");
    }

    [TestCase("another-key", TestName = "Given_ATokenBoundToTheBffsKey_When_AnotherKeyIsProved_Then_TheExchangeIsRefused")]
    [TestCase(null, TestName = "Given_ATokenBoundToTheBffsKey_When_NoKeyIsProved_Then_TheExchangeIsRefused")]
    public async Task Given_ATokenBoundToTheBffsKey_When_ItsKeyIsNotProved_Then_TheExchangeIsRefused(string? proofKey)
    {
        // Given
        var context = ExchangeBy("CustomerOnboardingWeb.ClientID", proofKey);
        var validator = ValidatorFor(SignInToken(cnfJkt: BffKey));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeTrue();
        context.Result.Error.Should().Be(OidcConstants.TokenErrors.InvalidGrant);
    }

    [Test]
    public async Task Given_ABearerToken_When_TheBffExchangesItWithAProof_Then_TheExchangeIsRefused()
    {
        // Given - a token that was never bound (no cnf): it could be a copy from anywhere.
        var context = ExchangeBy("CustomerOnboardingWeb.ClientID", proofKey: BffKey);
        var validator = ValidatorFor(SignInToken(cnfJkt: null));

        // When
        await validator.ValidateAsync(context, CancellationToken.None);

        // Then
        context.Result.IsError.Should().BeTrue();
        context.Result.Error.Should().Be(OidcConstants.TokenErrors.InvalidGrant);
    }

    // The agent's own sign-in token from the Customer Onboarding web app.
    private static Claim[] SignInToken(string? cnfJkt)
    {
        List<Claim> claims =
        [
            new(JwtClaimTypes.Subject, "agent-1"),
            new(JwtClaimTypes.ClientId, "CustomerOnboardingWeb.ClientID"),
            new(JwtClaimTypes.AuthenticationMethod, "pwd")
        ];
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