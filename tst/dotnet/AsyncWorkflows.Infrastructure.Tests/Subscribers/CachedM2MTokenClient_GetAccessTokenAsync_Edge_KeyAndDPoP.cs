using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;
using Moq;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Support;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Subscribers;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The IDP, played by WireMock; the REAL token client. A worker asks for its token with a
// │ client assertion (never a secret) whose audience is the issuer the IDP states, and a
// │ DPoP proof for the token endpoint the IDP names. A token that comes back unbound
// │ ("Bearer") is refused; a DPoP nonce request is answered once, with new JWTs; a refusal
// │ by the IDP is an error the message loop retries later. A good token is cached.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class CachedM2MTokenClient_GetAccessTokenAsync_Edge_KeyAndDPoP
{
    private const string Issuer = "https://idp.test";
    private const string TokenPath = "/connect/token";

    private WireMockNeighbour _idp = null!;
    private TestKeys _keys = null!;
    private TestSettings _settings = null!;
    private CachedM2MTokenClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _idp = new WireMockNeighbour();
        _idp.Answers("GET", "/.well-known/openid-configuration", 200, new { issuer = Issuer, token_endpoint = $"{IdpUrl}{TokenPath}" });
        _keys = new TestKeys();
        _settings = new TestSettings { IdentityServerAuthority = IdpUrl };

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(CachedM2MTokenClient.HttpClientName))
            .Returns(() => new HttpClient { BaseAddress = new Uri(IdpUrl) });
        _client = new CachedM2MTokenClient(factory.Object, _settings, _keys.Machine, NullLogger<CachedM2MTokenClient>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _idp.Dispose();
        _keys.Machine.Dispose();
    }

    private string IdpUrl => _idp.Url;

    private static object Token(string type) => new { access_token = "machine-token", token_type = type, expires_in = 3600 };

    [Test]
    public async Task Given_AnIdpThatBindsTokens_When_AWorkerAsksTwice_Then_ItProvesItselfWithItsKeyAndAProof_Once()
    {
        // Given
        _idp.Answers("POST", TokenPath, 200, Token("DPoP"));

        // When
        var first = await _client.GetAccessTokenAsync(CancellationToken.None);
        var second = await _client.GetAccessTokenAsync(CancellationToken.None);

        // Then
        first.Should().Be("machine-token");
        second.Should().Be(first);
        var request = _idp.Received(TokenPath).Should().ContainSingle("the token is cached until shortly before it expires").Subject;
        var form = TestKeys.Form(request.RequestMessage!.Body!);
        form.Should().NotContainKey("client_secret", "no shared secret exists any more");
        form["grant_type"].Should().Be("client_credentials");
        form["client_id"].Should().Be(_settings.ClientId);
        form["scope"].Should().Be(_settings.Scope);
        form["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        TestKeys.Read(form["client_assertion"]).Claims.GetProperty("aud").GetString()
            .Should().Be(Issuer, "the issuer as the IDP states it, not the address the worker was given");
        TestKeys.IsSignedBy(form["client_assertion"], _keys.ClientPublic).Should().BeTrue();

        var proof = WireMockNeighbour.Header(request, "DPoP");
        proof.Should().NotBeNull("the IDP binds the token only against a proof");
        TestKeys.Read(proof!).Claims.GetProperty("htu").GetString().Should().Be($"{IdpUrl}{TokenPath}");
        TestKeys.IsSignedBy(proof!, _keys.DPoPPublic).Should().BeTrue();
    }

    [Test]
    public async Task Given_AnIdpThatIssuesABearerToken_When_AWorkerAsks_Then_ItRefusesToUseIt()
    {
        // Given
        _idp.Answers("POST", TokenPath, 200, Token("Bearer"));

        // When
        var ask = () => _client.GetAccessTokenAsync(CancellationToken.None);

        // Then
        await ask.Should().ThrowAsync<InvalidOperationException>("an unbound token would work for whoever copies it");
    }

    [Test]
    public async Task Given_AnIdpThatAsksForANonce_When_AWorkerAsks_Then_ItAsksOnceMoreWithTheNonceAndNewJwts()
    {
        // Given
        _idp.Server.Given(Request.Create().WithPath(TokenPath).UsingPost()).InScenario("nonce").WillSetStateTo("asked")
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("DPoP-Nonce", "server-nonce")
                .WithBodyAsJson(new { error = "use_dpop_nonce" }));
        _idp.Server.Given(Request.Create().WithPath(TokenPath).UsingPost()).InScenario("nonce").WhenStateIs("asked")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Token("DPoP")));

        // When
        var token = await _client.GetAccessTokenAsync(CancellationToken.None);

        // Then
        token.Should().Be("machine-token");
        var requests = _idp.Received(TokenPath);
        requests.Should().HaveCount(2, "the nonce is answered once - never a loop");
        var second = TestKeys.Read(WireMockNeighbour.Header(requests[1], "DPoP")!).Claims;
        second.GetProperty("nonce").GetString().Should().Be("server-nonce");
        second.GetProperty("jti").GetString().Should().NotBe(
            TestKeys.Read(WireMockNeighbour.Header(requests[0], "DPoP")!).Claims.GetProperty("jti").GetString());
        TestKeys.Form(requests[1].RequestMessage!.Body!)["client_assertion"]
            .Should().NotBe(TestKeys.Form(requests[0].RequestMessage!.Body!)["client_assertion"], "each assertion is taken once");
    }

    [Test]
    public async Task Given_AnIdpThatRefusesTheClient_When_AWorkerAsks_Then_ItFailsWithoutAToken()
    {
        // Given
        _idp.Answers("POST", TokenPath, 401, new { error = "invalid_client" });

        // When
        var ask = () => _client.GetAccessTokenAsync(CancellationToken.None);

        // Then
        (await ask.Should().ThrowAsync<HttpRequestException>("the API is never called without a token"))
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}