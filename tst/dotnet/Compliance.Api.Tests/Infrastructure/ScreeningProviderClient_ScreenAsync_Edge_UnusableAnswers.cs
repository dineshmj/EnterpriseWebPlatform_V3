using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AwesomeAssertions;

using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Screening;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.Compliance.Api.Tests.Infrastructure;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The screening provider, played by WireMock; the REAL client with the API's own
// │ pipeline (ScreeningProviderRegistration). A verdict counts only when it is one of the
// │ known outcomes and names the screening; anything else is "unavailable" - the case
// │ stays in SCREENING and is retried, never passed.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class ScreeningProviderClient_ScreenAsync_Edge_UnusableAnswers
{
    private const string ScreeningsPath = "/v1/screenings";
    private const string ApiKey = "test-screening-key";

    private WireMockNeighbour _provider = null!;
    private IScreeningProvider _client = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new WireMockNeighbour();
        _client = ClientTo(_provider.Url);
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    [Test]
    [Category("Security")]
    public async Task Given_TheProviderAnswers_When_AnApplicantIsScreened_Then_OnlyTheNameAndAddressLeaveEwp()
    {
        // Given
        var request = Applicant();
        _provider.Answers("POST", ScreeningsPath, 200, new { screeningId = "SCR-1001", provider = "WorldCheck-Sim", outcome = "POTENTIAL_MATCH" });

        // When
        var response = await _client.ScreenAsync(request, CancellationToken.None);

        // Then: the verdict, and data minimisation towards a third party - no contact details.
        response.Should().Be(new ScreeningResponse(ScreeningOutcome.PotentialMatch, "WorldCheck-Sim", "SCR-1001"));
        var sent = _provider.Received(ScreeningsPath).Single();
        WireMockNeighbour.Header(sent, "X-Api-Key").Should().Be(ApiKey);
        var body = JsonNode.Parse(sent.RequestMessage!.Body!)!.AsObject();
        body.Select(p => p.Key).Should().BeEquivalentTo("customerNumber", "applicationNumber", "requestId", "subject", "lists");
        body["subject"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(
            "firstName", "lastName", "addressLine1", "city", "state", "postalCode", "countryCode");
        body["lists"]!.AsArray().Select(l => l!.GetValue<string>()).Should().Equal("AML", "SANCTIONS", "PEP");
    }

    [Test]
    public async Task Given_TheProviderFailsOnceThenAnswers_When_AnApplicantIsScreened_Then_TheVerdictIsUsed()
    {
        // Given
        _provider.AnswersInTurn("POST", ScreeningsPath,
            (503, null), (200, new { screeningId = "SCR-1002", provider = "WorldCheck-Sim", outcome = "CLEAR" }));

        // When
        var response = await _client.ScreenAsync(Applicant(), CancellationToken.None);

        // Then
        response.Outcome.Should().Be(ScreeningOutcome.Clear);
        _provider.Received(ScreeningsPath).Should().HaveCount(2);
    }

    [TestCase(200, """{"screeningId":"SCR-1003","provider":"WorldCheck-Sim","outcome":"PROBABLY_FINE"}""")]
    [TestCase(200, """{"screeningId":"","provider":"WorldCheck-Sim","outcome":"CLEAR"}""")]
    [TestCase(200, """{"outcome":"CLEAR"}""")]
    [TestCase(200, "<html>Service page</html>")]
    [TestCase(200, "")]
    [TestCase(500, null)]
    public async Task Given_AnUnusableAnswer_When_AnApplicantIsScreened_Then_TheProviderIsUnavailableNeverAPass(int status, string? body)
    {
        // Given
        _provider.Answers("POST", ScreeningsPath, status, body);

        // When
        var screen = () => _client.ScreenAsync(Applicant(), CancellationToken.None);

        // Then: the case stays in SCREENING with this reason, and is retried later.
        await screen.Should().ThrowAsync<ScreeningUnavailableException>();
    }

    [Test]
    public async Task Given_EveryAnswerIsLate_When_AnApplicantIsScreened_Then_TheProviderIsUnavailable()
    {
        // Given
        _provider.Answers("POST", ScreeningsPath, 200, new { screeningId = "SCR-1004", provider = "WorldCheck-Sim", outcome = "CLEAR" },
            after: WireMockNeighbour.AttemptTimeout * 2);

        // When
        var screen = () => _client.ScreenAsync(Applicant(), CancellationToken.None);

        // Then
        await screen.Should().ThrowAsync<ScreeningUnavailableException>();
        (await _provider.ReceivedAsync(ScreeningsPath, count: 3)).Should().HaveCount(3);
    }

    [Test]
    [Category("Security")]
    public async Task Given_TheProviderRedirects_When_AnApplicantIsScreened_Then_TheRedirectIsNotFollowedAndTheDataStaysHere()
    {
        // Given: an answer that points the applicant's details, and the API key, at another host.
        using var elsewhere = new WireMockNeighbour();
        elsewhere.Answers("POST", ScreeningsPath, 200, new { screeningId = "SCR-ELSEWHERE", provider = "Elsewhere", outcome = "CLEAR" });
        _provider.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(ScreeningsPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(307).WithHeader("Location", $"{elsewhere.Url}{ScreeningsPath}"));

        // When
        var screen = () => _client.ScreenAsync(Applicant(), CancellationToken.None);

        // Then
        await screen.Should().ThrowAsync<ScreeningUnavailableException>().WithMessage("*HTTP 307*");
        elsewhere.Received(ScreeningsPath).Should().BeEmpty();
    }

    [Test]
    public async Task Given_TheProviderIsUnreachable_When_AnApplicantIsScreened_Then_ItIsUnavailable()
    {
        // Given
        _provider.Server.Stop();

        // When
        var screen = () => _client.ScreenAsync(Applicant(), CancellationToken.None);

        // Then
        await screen.Should().ThrowAsync<ScreeningUnavailableException>();
    }

    private static IScreeningProvider ClientTo(string baseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ScreeningProviderOptions { BaseUrl = baseUrl, ApiKey = ApiKey }));
        services.AddScreeningProviderClient();
        WireMockNeighbour.ShortenWaits(services, ScreeningProviderClient.HttpClientName);
        return services.BuildServiceProvider().GetRequiredService<IScreeningProvider>();
    }

    private static ScreeningRequest Applicant() => new(
        "CUS-261028-WM0001", "APP-261028-WM0001", Guid.NewGuid(), "Sophie", "Turner", "12 Harbour St", "Sydney", "NSW", "2000", "AU");
}