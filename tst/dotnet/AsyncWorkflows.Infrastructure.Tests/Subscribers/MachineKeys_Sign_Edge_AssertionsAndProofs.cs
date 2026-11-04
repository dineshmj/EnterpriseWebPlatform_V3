using System.Security.Cryptography;

using AwesomeAssertions;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Support;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Subscribers;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ What the IDP and the APIs check, checked here first: the client assertion names the
// │ worker and the IDP's issuer only, lives 60 s, is signed by the CLIENT key and carries
// │ its thumbprint as kid; a DPoP proof is signed by the OTHER key, carries only its public
// │ part, names exactly the request (method, address without the query), and the token's
// │ hash on an API call. Every JWT has a jti of its own - the receivers accept each once.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class MachineKeys_Sign_Edge_AssertionsAndProofs
{
    private const string ClientId = "Test.Subscriber.M2M.ClientID";
    private const string Issuer = "https://idp.test";

    private TestKeys _keys = null!;

    [SetUp]
    public void SetUp() => _keys = new TestKeys();

    [TearDown]
    public void TearDown() => _keys.Machine.Dispose();

    [Test]
    public void Given_AWorker_When_ItSignsAClientAssertion_Then_OnlyThisIssuerTakesItFor60SecondsAsThisClient()
    {
        // Given / When
        var assertion = _keys.Machine.ClientAssertion(ClientId, Issuer);

        // Then
        var (header, claims) = TestKeys.Read(assertion);
        header.GetProperty("alg").GetString().Should().Be("ES256");
        header.GetProperty("typ").GetString().Should().Be("client-authentication+jwt", "the IDP checks it strictly");
        header.GetProperty("kid").GetString().Should().Be(TestKeys.Thumbprint(_keys.ClientPublic), "the kid New-EwpClientKeys.mjs gave the public key");
        claims.GetProperty("iss").GetString().Should().Be(ClientId);
        claims.GetProperty("sub").GetString().Should().Be(ClientId);
        claims.GetProperty("aud").GetString().Should().Be(Issuer, "the issuer only - never another endpoint");
        (claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64()).Should().Be(60);
        TestKeys.IsSignedBy(assertion, _keys.ClientPublic).Should().BeTrue();
        TestKeys.IsSignedBy(assertion, _keys.DPoPPublic).Should().BeFalse("the client key signs who the worker is, never the DPoP key");
    }

    [Test]
    public void Given_AWorker_When_ItSignsTwoAssertions_Then_EachHasAJtiOfItsOwn()
    {
        // Given / When
        var first = TestKeys.Read(_keys.Machine.ClientAssertion(ClientId, Issuer)).Claims.GetProperty("jti").GetString();
        var second = TestKeys.Read(_keys.Machine.ClientAssertion(ClientId, Issuer)).Claims.GetProperty("jti").GetString();

        // Then
        first.Should().NotBe(second, "the IDP takes each assertion once - a repeated jti is a replay");
    }

    [Test]
    public void Given_AnApiCall_When_ItsProofIsSigned_Then_ItNamesExactlyThatRequestAndToken()
    {
        // Given
        const string token = "access-token";

        // When
        var proof = _keys.Machine.Proof(HttpMethod.Post, new Uri("https://api.test/internal/v1/things?page=2"), token);

        // Then
        var (header, claims) = TestKeys.Read(proof);
        header.GetProperty("typ").GetString().Should().Be("dpop+jwt");
        header.GetProperty("alg").GetString().Should().Be("ES256");
        header.GetProperty("jwk").TryGetProperty("d", out _).Should().BeFalse("only the PUBLIC part travels");
        claims.GetProperty("htm").GetString().Should().Be("POST");
        claims.GetProperty("htu").GetString().Should().Be("https://api.test/internal/v1/things", "the address without its query (RFC 9449 section 4.2)");
        claims.GetProperty("ath").GetString().Should().Be(TestKeys.HashOf(token), "the proof is tied to this token");
        claims.TryGetProperty("nonce", out _).Should().BeFalse();
        TestKeys.IsSignedBy(proof, _keys.DPoPPublic).Should().BeTrue();
        _keys.Machine.DPoPThumbprint.Should().Be(TestKeys.Thumbprint(_keys.DPoPPublic), "the cnf.jkt the IDP binds the token to");
    }

    [Test]
    public void Given_ATokenRequest_When_ItsProofIsSigned_Then_ItHasNoTokenHashButTheServersNonce()
    {
        // Given / When
        var proof = _keys.Machine.Proof(HttpMethod.Post, new Uri("https://idp.test/connect/token"), nonce: "server-nonce");

        // Then
        var claims = TestKeys.Read(proof).Claims;
        claims.TryGetProperty("ath", out _).Should().BeFalse("there is no token yet");
        claims.GetProperty("nonce").GetString().Should().Be("server-nonce");
    }

    [Test]
    public void Given_OneKeyForBoth_When_TheKeysAreLoaded_Then_TheWorkerRefuses()
    {
        // Given
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sameKey = ECDsa.Create(key.ExportParameters(includePrivateParameters: true));

        // When
        var load = () => new MachineKeys(key, sameKey, TimeProvider.System);

        // Then
        load.Should().Throw<InvalidOperationException>("\"I am this client\" and \"I hold this token\" stay two keys");
    }
}