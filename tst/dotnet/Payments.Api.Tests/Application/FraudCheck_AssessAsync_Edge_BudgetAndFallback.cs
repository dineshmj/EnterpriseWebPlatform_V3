using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;
using Moq;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Application.Fraud;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Application;

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ The fraud check keeps its latency budget whatever the provider does, and its fallback
// │ is cautious: a payee this customer never paid, or AUD 500 and more, goes to an officer;
// │ a payee already paid with a small amount follows its tier; it never declines. Only the
// │ caller giving up stops the check - that is not a fallback decision.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class FraudCheck_AssessAsync_Edge_BudgetAndFallback
{
    private static readonly FraudCheckPolicy Policy = new(TimeSpan.FromMilliseconds(200), 500m);

    private Mock<IFraudScoring> _scoring = null!;
    private Mock<IPaymentRepository> _payments = null!;
    private TestMeterFactory _meters = null!;
    private FraudCheck _check = null!;

    [SetUp]
    public void SetUp()
    {
        _scoring = new Mock<IFraudScoring>();
        _payments = new Mock<IPaymentRepository>();
        _meters = new TestMeterFactory();
        _check = new FraudCheck(_scoring.Object, _payments.Object, Policy, _meters, NullLogger<FraudCheck>.Instance);
    }

    [TearDown]
    public void TearDown() => _meters.Dispose();

    private void PaidBefore(bool paid) =>
        _payments.Setup(p => p.HasPaidBeforeAsync("CUST-100001", PaymentsTestData.Payee, It.IsAny<CancellationToken>())).ReturnsAsync(paid);

    private void ProviderIsDown() =>
        _scoring.Setup(s => s.ScoreAsync(It.IsAny<FraudScoringRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FraudScoringUnavailableException("HTTP 503"));

    private static FraudScoringRequestDraft Draft(decimal amount = 140m) => new(
        Guid.NewGuid(), "PAY-261028-FRAUD1", "CUST-100001", PaymentsTestData.Payer, "Harbour Plumbing Pty Ltd", PaymentsTestData.Payee, amount);

    [Test]
    [Category("Happy")]
    public async Task Given_TheProviderAnswersInTime_When_APaymentIsChecked_Then_ItsAnswerIsKeptAndItWasToldTheyAreANewPayee()
    {
        // Given
        PaidBefore(false);
        FraudScoringRequest? asked = null;
        _scoring.Setup(s => s.ScoreAsync(It.IsAny<FraudScoringRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FraudScoringRequest, CancellationToken>((r, _) => asked = r)
            .ReturnsAsync(new FraudScoringResult(680, FraudDecision.Review, ["NEW_PAYEE"]));

        // When
        var assessment = await _check.AssessAsync(Draft(600m), CancellationToken.None);

        // Then
        assessment.Should().Be(FraudAssessment.FromProvider(FraudDecision.Review, 680, ["NEW_PAYEE"]));
        asked!.FirstPaymentToPayee.Should().BeTrue();
        asked.Amount.Should().Be(600m);
        asked.Currency.Should().Be("AUD");
    }

    [Test]
    [Category("Performance")]
    public async Task Given_TheProviderHangs_When_APaymentIsChecked_Then_TheFallbackDecidesWithinTheBudget()
    {
        // Given: a provider that would answer after 5 s, if anyone waited.
        PaidBefore(true);
        _scoring.Setup(s => s.ScoreAsync(It.IsAny<FraudScoringRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (FraudScoringRequest _, CancellationToken token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return new FraudScoringResult(1, FraudDecision.Allow, []);
            });
        var watch = Stopwatch.StartNew();

        // When
        var assessment = await _check.AssessAsync(Draft(), CancellationToken.None);

        // Then
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        assessment.Source.Should().Be(FraudSource.Fallback);
        assessment.Decision.Should().Be(FraudDecision.Allow);
    }

    [TestCase(false, 140.00, FraudDecision.Review, "PROVIDER_UNAVAILABLE, NEW_PAYEE")]
    [TestCase(true, 499.99, FraudDecision.Allow, "PROVIDER_UNAVAILABLE")]
    [TestCase(true, 500.00, FraudDecision.Review, "PROVIDER_UNAVAILABLE, AMOUNT_AT_OR_ABOVE_FALLBACK_LIMIT")]
    [TestCase(false, 900.00, FraudDecision.Review, "PROVIDER_UNAVAILABLE, NEW_PAYEE, AMOUNT_AT_OR_ABOVE_FALLBACK_LIMIT")]
    [Category("Boundary")]
    public async Task Given_TheProviderIsDown_When_APaymentIsChecked_Then_TheFallbackIsCautiousButNeverDeclines(
        bool paidBefore, decimal amount, FraudDecision decision, string reasons)
    {
        // Given
        PaidBefore(paidBefore);
        ProviderIsDown();

        // When
        var assessment = await _check.AssessAsync(Draft(amount), CancellationToken.None);

        // Then
        assessment.Should().Be(FraudAssessment.FromFallback(decision, reasons.Split(", ")));
        assessment.Score.Should().BeNull();
    }

    [Test]
    public async Task Given_TheCallerGivesUp_When_APaymentIsChecked_Then_TheCheckStops_WithoutAFallbackDecision()
    {
        // Given
        PaidBefore(true);
        using var gaveUp = new CancellationTokenSource();
        _scoring.Setup(s => s.ScoreAsync(It.IsAny<FraudScoringRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (FraudScoringRequest _, CancellationToken token) =>
            {
                await gaveUp.CancelAsync();
                token.ThrowIfCancellationRequested();
                return new FraudScoringResult(1, FraudDecision.Allow, []);
            });

        // When
        var check = () => _check.AssessAsync(Draft(), gaveUp.Token);

        // Then
        await check.Should().ThrowAsync<OperationCanceledException>();
    }
}