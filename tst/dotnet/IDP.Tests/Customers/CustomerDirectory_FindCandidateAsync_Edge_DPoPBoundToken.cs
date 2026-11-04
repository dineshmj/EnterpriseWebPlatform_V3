using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Customers;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Customers;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The IDP's own token for Customer Onboarding is DPoP-bound like every other: every
// │ Domain API refuses a token without cnf. The token names a key (cnf.jkt), the call says
// │ "DPoP" and carries a proof signed by THAT key, for exactly this method and address and
// │ this token (ath) - and each call has a proof of its own.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class CustomerDirectory_FindCandidateAsync_Edge_DPoPBoundToken
{
    private const string BaseAddress = "https://customer-onboarding-api.ewp.example/";

    [Test]
    public async Task Given_ARegistration_When_TheIdpAsksCustomerOnboarding_Then_ItsTokenIsBoundToTheKeyThatSignsTheProof()
    {
        // Given
        var (directory, tokens, handler) = Directory();

        // When
        await directory.FindCandidateAsync("C-1", "a@b.example", CancellationToken.None);

        // Then
        var request = handler.Requests.Should().ContainSingle().Subject;
        var accessToken = tokens.Issued.Should().ContainSingle().Subject;
        request.Scheme.Should().Be("DPoP", "a bound token is never sent as Bearer");
        request.Token.Should().Be(accessToken);

        var (header, claims) = Verified(request.Proof);
        var jkt = JsonDocument.Parse(tokens.Tokens.Single().Confirmation!).RootElement.GetProperty("jkt").GetString();
        jkt.Should().Be(Thumbprint(header.GetProperty("jwk")), "the token is bound to the key that signs the proof");
        header.GetProperty("typ").GetString().Should().Be("dpop+jwt");
        claims.GetProperty("htm").GetString().Should().Be("POST");
        claims.GetProperty("htu").GetString().Should().Be($"{BaseAddress}internal/v1/customers/online-identity/candidates");
        claims.GetProperty("ath").GetString().Should().Be(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))));
    }

    [Test]
    public async Task Given_TwoCalls_When_TheIdpAsksCustomerOnboarding_Then_EachCarriesAProofOfItsOwnForTheSameKey()
    {
        // Given
        var (directory, tokens, handler) = Directory();

        // When
        await directory.FindCandidateAsync("C-1", "a@b.example", CancellationToken.None);
        await directory.LinkAsync("C-1", "a@b.example", Guid.NewGuid(), CancellationToken.None);

        // Then
        var proofs = handler.Requests.Select(r => Verified(r.Proof)).ToList();
        proofs.Select(p => p.Claims.GetProperty("jti").GetString()).Should().OnlyHaveUniqueItems("a proof is used once");
        tokens.Tokens.Select(t => t.Confirmation).Distinct().Should().ContainSingle("one key per IDP process");
    }

    private static (CustomerDirectory Directory, FakeTokenService Tokens, RecordingHandler Handler) Directory()
    {
        var tokens = new FakeTokenService();
        var handler = new RecordingHandler();
        var clientIds = ClientIds.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientIds:IdpCustomerDirectory"] = "IdentityServer.CustomerDirectory.ClientID" })
            .Build());
        var directory = new CustomerDirectory(
            new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) },
            tokens,
            new FixedIssuer(),
            clientIds,
            TimeProvider.System,
            NullLogger<CustomerDirectory>.Instance);
        return (directory, tokens, handler);
    }

    // Checks the proof's ES256 signature with the key in its own header; returns header and claims.
    private static (JsonElement Header, JsonElement Claims) Verified(string proof)
    {
        var parts = proof.Split('.');
        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
        var jwk = header.GetProperty("jwk");
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()), Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()) }
        });
        key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256)
            .Should().BeTrue("the proof is signed by the key it names");
        return (header, JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement);
    }

    // RFC 7638: SHA-256 over the required members, in lexical order, without spaces.
    private static string Thumbprint(JsonElement jwk) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{jwk.GetProperty("x").GetString()}\",\"y\":\"{jwk.GetProperty("y").GetString()}\"}}")));

    private sealed class FakeTokenService : ITokenService
    {
        public List<Token> Tokens { get; } = [];

        public List<string> Issued { get; } = [];

        public Task<string> CreateSecurityTokenAsync(Token token, CancellationToken ct)
        {
            Tokens.Add(token);
            Issued.Add($"access-token-{Issued.Count + 1}");
            return Task.FromResult(Issued[^1]);
        }

        public Task<Token> CreateIdentityTokenAsync(TokenCreationRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<Token> CreateAccessTokenAsync(TokenCreationRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FixedIssuer : IIssuerNameService
    {
        public Task<string> GetCurrentAsync(CancellationToken ct) => Task.FromResult("https://idp.ewp.example");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Scheme, string Token, string Proof)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter!, request.Headers.GetValues("DPoP").Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}