using Microsoft.IdentityModel.JsonWebTokens;

using AwesomeAssertions;

using EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;
using EnterpriseWebPlatform.PartnerProducts.Api.Tests.Support;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Tests.PartnerBank;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Only a FINAL answer about the customer is Refused (400 / 404 / 409, with the Partner
// │ Bank's own reason). Everything else - no answer, a late answer, 5xx, our own token
// │ refused - is Unavailable: not final, the caller sends the SAME instruction again,
// │ and the same jti makes that resend safe (the Partner Bank does each instruction once).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class PartnerBankClient_PlacePurchaseAsync_Edge_LostAndRefusedAnswers
{
    private PartnerBankStub _partnerBank = null!;

    [SetUp]
    public void SetUp() => _partnerBank = new PartnerBankStub(timeout: TimeSpan.FromSeconds(1)).IssuesTokens();

    [TearDown]
    public void TearDown() => _partnerBank.Dispose();

    private Task<PartnerAnswer> PlaceAsync(Guid instructionId) =>
        _partnerBank.Client.PlacePurchaseAsync(PartnerBankStub.PurchaseInstruction(), instructionId, CancellationToken.None);

    [Test]
    public async Task Given_TheAnswerArrivesAfterTheTimeout_When_ThePurchaseIsResent_Then_BothAttemptsCarryTheSameJti()
    {
        // Given: the Partner Bank receives the order, but its answer comes too late - lost to EWP.
        var instructionId = Guid.NewGuid();
        _partnerBank.AnswersPurchase(201, new { orderNumber = "PB-ORD-000010" }, after: TimeSpan.FromSeconds(3));

        // When: the first attempt gives up; the order is sent again (as the application does).
        var first = await PlaceAsync(instructionId);
        _partnerBank.Server.ResetMappings();
        _partnerBank.IssuesTokens().AnswersPurchase(200, new { orderNumber = "PB-ORD-000010" });   // 200: "already done"
        var second = await PlaceAsync(instructionId);

        // Then: not a refusal the first time, accepted the second - and the Partner Bank saw ONE instruction ID.
        first.Outcome.Should().Be(PartnerOutcome.Unavailable);
        second.Outcome.Should().Be(PartnerOutcome.Accepted);
        var jtis = (await _partnerBank.ReceivedAsync(PartnerBankStub.GoldOrdersPath, count: 2))
            .Select(e => new JsonWebToken(e.RequestMessage!.Body!).Id).ToList();
        jtis.Should().HaveCount(2).And.OnlyContain(jti => jti == instructionId.ToString());
    }

    [TestCase(409, "The order was already allocated.")]
    [TestCase(404, "No Digital Gold account PB-DGA-TEST01.")]
    public async Task Given_ThePartnerBankRefusesWithAProblem_When_APurchaseIsPlaced_Then_ItIsRefusedWithThePartnerBanksReason(int status, string detail)
    {
        // Given
        _partnerBank.AnswersPurchase(status, new { title = "Refused", detail });

        // When
        var answer = await PlaceAsync(Guid.NewGuid());

        // Then
        answer.Outcome.Should().Be(PartnerOutcome.Refused);
        answer.Reason.Should().Be(detail);
    }

    [Test]
    public async Task Given_AValidationProblem_When_APurchaseIsPlaced_Then_ItIsRefusedWithTheFirstMessage()
    {
        // Given
        _partnerBank.AnswersPurchase(400, new { title = "Invalid", errors = new { amount = new[] { "The amount must be at least AUD 10." } } });

        // When
        var answer = await PlaceAsync(Guid.NewGuid());

        // Then
        answer.Outcome.Should().Be(PartnerOutcome.Refused);
        answer.Reason.Should().Be("The amount must be at least AUD 10.");
    }

    [TestCase(500)]
    [TestCase(503)]
    public async Task Given_ThePartnerBankFails_When_APurchaseIsPlaced_Then_ItIsUnavailableNotRefused(int status)
    {
        // Given
        _partnerBank.AnswersPurchase(status, new { detail = "Internal error at the Partner Bank." });

        // When
        var answer = await PlaceAsync(Guid.NewGuid());

        // Then: a failure at the Partner Bank says nothing about the customer - never a final refusal.
        answer.Outcome.Should().Be(PartnerOutcome.Unavailable);
        answer.Body.Should().BeNull("the Partner Bank's internal error text is not passed on");
    }

    [Test]
    public async Task Given_ThePartnerBankIsUnreachable_When_APurchaseIsPlaced_Then_ItIsUnavailable()
    {
        // Given: nobody listens any more.
        _partnerBank.Server.Stop();

        // When
        var answer = await PlaceAsync(Guid.NewGuid());

        // Then
        answer.Outcome.Should().Be(PartnerOutcome.Unavailable);
    }

    [Test]
    [Category("Security")]
    public async Task Given_OurTokenIsRejected_When_ThePurchaseIsSentAgain_Then_ANewTokenIsFetchedFirst()
    {
        // Given: the Partner Bank no longer accepts EWP's cached token.
        var instructionId = Guid.NewGuid();
        _partnerBank.AnswersPurchase(401);

        // When
        var first = await PlaceAsync(instructionId);
        _partnerBank.Server.ResetMappings();
        _partnerBank.IssuesTokens("partner-token-2").AnswersPurchase(201, new { orderNumber = "PB-ORD-000011" });
        var second = await PlaceAsync(instructionId);

        // Then: the rejected token is dropped, not reused.
        first.Outcome.Should().Be(PartnerOutcome.Unavailable);
        second.Outcome.Should().Be(PartnerOutcome.Accepted);
        _partnerBank.Received(PartnerBankStub.TokenPath).Should().HaveCount(2);
        _partnerBank.Received(PartnerBankStub.GoldOrdersPath).Last().RequestMessage!.Headers!["Authorization"].Single()
            .Should().Be("Bearer partner-token-2");
    }

    [Test]
    [Category("Security")]
    public async Task Given_TheTokenEndpointRefusesEwp_When_APurchaseIsPlaced_Then_ItIsUnavailableAndNoInstructionIsSent()
    {
        // Given
        _partnerBank.Server.ResetMappings();
        _partnerBank.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(PartnerBankStub.TokenPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(400).WithBodyAsJson(new { error = "invalid_client" }));

        // When
        var answer = await PlaceAsync(Guid.NewGuid());

        // Then: without its own token EWP sends nothing.
        answer.Outcome.Should().Be(PartnerOutcome.Unavailable);
        _partnerBank.Received(PartnerBankStub.GoldOrdersPath).Should().BeEmpty();
    }
}