using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Support;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Subscribers;

// ┌─ SEC + RESIL ────────────────────────────────────────────────────────────────────────
// │ An API (and the IDP), played by WireMock; the REAL handler registered the way every
// │ subscriber registers it - AFTER the standard resilience handler. The API answers 503
// │ once: the retry must carry the same DPoP-bound token with a NEW proof, because the API
// │ takes each proof once. Had the handler sat outside the pipeline, the retry would
// │ resend the first proof and be refused.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class DPoPMachineTokenHandler_SendAsync_Edge_FreshProofPerAttempt
{
    private const string ApiClient = "Api";
    private const string ApiPath = "/internal/v1/things";

    private WireMockNeighbour _neighbour = null!;
    private TestKeys _keys = null!;
    private ServiceProvider _services = null!;

    [SetUp]
    public void SetUp()
    {
        // One WireMock plays both: the IDP's discovery and token endpoint, and the API.
        _neighbour = new WireMockNeighbour();
        _neighbour
            .Answers("GET", "/.well-known/openid-configuration", 200, new { issuer = "https://idp.test", token_endpoint = $"{_neighbour.Url}/connect/token" })
            .Answers("POST", "/connect/token", 200, new { access_token = "machine-token", token_type = "DPoP", expires_in = 3600 })
            .AnswersInTurn("POST", ApiPath, (503, null), (200, new { }));
        _keys = new TestKeys();

        var services = new ServiceCollection();
        services.AddSingleton<ILogger<CachedM2MTokenClient>>(NullLogger<CachedM2MTokenClient>.Instance);
        services.AddSingleton(_keys.Machine);
        services.AddSingleton<IM2MClientSettings>(new TestSettings());
        services.AddSingleton<CachedM2MTokenClient>();
        services.AddHttpClient(CachedM2MTokenClient.HttpClientName, c => c.BaseAddress = new Uri(_neighbour.Url));
        services.AddHttpClient(ApiClient, c => c.BaseAddress = new Uri(_neighbour.Url)).AddStandardResilienceHandler();
        WireMockNeighbour.ShortenWaits(services, ApiClient);
        services.AddHttpClient(ApiClient).AddDPoPMachineToken();
        _services = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _services.Dispose();
        _neighbour.Dispose();
    }

    [Test]
    public async Task Given_AnApiCallThatIsRetried_When_ItIsSent_Then_EachAttemptCarriesTheBoundTokenWithAProofOfItsOwn()
    {
        // Given
        var client = _services.GetRequiredService<IHttpClientFactory>().CreateClient(ApiClient);

        // When
        using var response = await client.PostAsync(ApiPath, new StringContent("{}"));

        // Then
        response.IsSuccessStatusCode.Should().BeTrue("the retry got through");
        var attempts = _neighbour.Received(ApiPath);
        attempts.Should().HaveCount(2);
        foreach (var attempt in attempts)
        {
            WireMockNeighbour.Header(attempt, "Authorization").Should().Be("DPoP machine-token", "never Bearer");
            var proof = WireMockNeighbour.Header(attempt, "DPoP")!;
            var claims = TestKeys.Read(proof).Claims;
            claims.GetProperty("htm").GetString().Should().Be("POST");
            claims.GetProperty("htu").GetString().Should().Be($"{_neighbour.Url}{ApiPath}");
            claims.GetProperty("ath").GetString().Should().Be(TestKeys.HashOf("machine-token"));
            TestKeys.IsSignedBy(proof, _keys.DPoPPublic).Should().BeTrue();
        }
        TestKeys.Read(WireMockNeighbour.Header(attempts[0], "DPoP")!).Claims.GetProperty("jti").GetString()
            .Should().NotBe(TestKeys.Read(WireMockNeighbour.Header(attempts[1], "DPoP")!).Claims.GetProperty("jti").GetString(),
                "the API takes each proof once - a retry with the first one would be refused");
        _neighbour.Received("/connect/token").Should().ContainSingle("both attempts use the one cached token");
    }
}