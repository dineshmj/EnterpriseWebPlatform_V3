using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;
using Moq;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Application.Fraud;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Application;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ One payment per Idempotency-Key: a resend of the SAME request answers with the first
// │ payment (never pays twice); the same key with other details, or someone else's key,
// │ is a conflict - never silently answered with a payment the caller did not ask for.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class InitiatePaymentCommandHandler_HandleAsync_Edge_Idempotency
{
    private Mock<IPaymentRepository> _payments = null!;
    private Mock<IPaymentSagaRepository> _sagas = null!;
    private Mock<IPaymentsUnitOfWork> _unitOfWork = null!;
    private InitiatePaymentCommandHandler _handler = null!;
    private TestMeterFactory _meters = null!;

    [SetUp]
    public void SetUp()
    {
        _payments = new Mock<IPaymentRepository>();
        _sagas = new Mock<IPaymentSagaRepository>();
        _unitOfWork = new Mock<IPaymentsUnitOfWork>();
        _unitOfWork.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var clock = new Mock<TimeProvider>();
        clock.Setup(c => c.GetUtcNow()).Returns(PaymentsTestData.Now);

        // The fraud scoring allows every payment here: these tests are about the Idempotency-Key.
        var scoring = new Mock<IFraudScoring>();
        scoring.Setup(s => s.ScoreAsync(It.IsAny<FraudScoringRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FraudScoringResult(120, FraudDecision.Allow, []));
        _meters = new TestMeterFactory();
        var fraudCheck = new FraudCheck(scoring.Object, _payments.Object, new FraudCheckPolicy(TimeSpan.FromMilliseconds(250), 500m),
            _meters, NullLogger<FraudCheck>.Instance);

        _handler = new InitiatePaymentCommandHandler(
            _payments.Object, _sagas.Object, _unitOfWork.Object, new ApprovalTier(PaymentsTestData.ApprovalThreshold),
            fraudCheck, PaymentsTestData.Policy, clock.Object, NullLogger<InitiatePaymentCommandHandler>.Instance);
    }

    [TearDown]
    public void TearDown() => _meters.Dispose();

    private static InitiatePaymentCommand CommandFor(Payment payment, decimal? amount = null, string? initiatedBy = null) =>
        new(payment.PaymentRef, payment.CustomerNumber, payment.From.Bsb, payment.From.AccountNumber, payment.PayeeName,
            payment.To.Bsb, payment.To.AccountNumber, amount ?? payment.Amount, payment.Reference, payment.BranchCode,
            initiatedBy ?? payment.InitiatedByUserId);

    [Test]
    [Category("Happy")]
    public async Task Given_ANewIdempotencyKey_When_ThePaymentIsInitiated_Then_ItIsCreatedAndItsSagaStarts()
    {
        // Given
        var paymentRef = Guid.NewGuid();
        _payments.Setup(p => p.GetByRefAsync(paymentRef, It.IsAny<CancellationToken>())).ReturnsAsync((Payment?)null);
        Payment? added = null;
        _payments.Setup(p => p.Add(It.IsAny<Payment>())).Callback<Payment>(p => added = p);
        // The first save gives the payment its ID (as the database does).
        _unitOfWork.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .Callback(() => typeof(Payment).GetProperty(nameof(Payment.Id))!.SetValue(added, 7L))
            .Returns(Task.CompletedTask);
        var command = CommandFor(PaymentsTestData.NewPayment(paymentRef: paymentRef));

        // When
        var result = await _handler.HandleAsync(command, CancellationToken.None);

        // Then
        result.Created.Should().BeTrue();
        result.PaymentId.Should().Be(7);
        result.PaymentNumber.Should().MatchRegex("^PAY-261015-[2-9A-HJ-NP-Z]{6}$");
        result.Status.Should().Be("RESERVING_FUNDS");
        _sagas.Verify(s => s.Add(It.Is<PaymentSaga>(saga => saga.PaymentRef == paymentRef)), Times.Once);
    }

    [Test]
    [Category("Resilience")]
    public async Task Given_TheSameRequestAgain_When_ItIsResent_Then_TheFirstPaymentIsReturnedAndNothingNewIsCreated()
    {
        // Given
        var existing = PaymentsTestData.NewPayment();
        _payments.Setup(p => p.GetByRefAsync(existing.PaymentRef, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        // When
        var result = await _handler.HandleAsync(CommandFor(existing), CancellationToken.None);

        // Then
        result.Created.Should().BeFalse();
        result.PaymentId.Should().Be(existing.Id);
        result.PaymentNumber.Should().Be(existing.PaymentNumber);
        _payments.Verify(p => p.Add(It.IsAny<Payment>()), Times.Never);
        _sagas.Verify(s => s.Add(It.IsAny<PaymentSaga>()), Times.Never);
    }

    [Test]
    public async Task Given_TheSameRequestWrittenDifferently_When_ItIsResent_Then_ItStillCountsAsTheSamePayment()
    {
        // Given: the BSB without its dash and the payee name with extra spaces.
        var existing = PaymentsTestData.NewPayment();
        _payments.Setup(p => p.GetByRefAsync(existing.PaymentRef, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        var command = CommandFor(existing) with { FromBsb = "062000", PayeeName = "  Harbour   Plumbing Pty Ltd " };

        // When
        var result = await _handler.HandleAsync(command, CancellationToken.None);

        // Then
        result.Created.Should().BeFalse();
    }

    [Test]
    public async Task Given_TheSameKeyWithAnotherAmount_When_ItIsSent_Then_ItIsAConflict()
    {
        // Given
        var existing = PaymentsTestData.NewPayment(140m);
        _payments.Setup(p => p.GetByRefAsync(existing.PaymentRef, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        // When
        var initiate = () => _handler.HandleAsync(CommandFor(existing, amount: 141m), CancellationToken.None);

        // Then
        await initiate.Should().ThrowAsync<DomainConflictException>().WithMessage("*other details*");
        _payments.Verify(p => p.Add(It.IsAny<Payment>()), Times.Never);
    }

    [Test]
    public async Task Given_SomeoneElsesKey_When_AnotherPersonSendsIt_Then_ItIsAConflict()
    {
        // Given
        var existing = PaymentsTestData.NewPayment();
        _payments.Setup(p => p.GetByRefAsync(existing.PaymentRef, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        // When
        var initiate = () => _handler.HandleAsync(CommandFor(existing, initiatedBy: "sub-mia"), CancellationToken.None);

        // Then
        await initiate.Should().ThrowAsync<DomainConflictException>().WithMessage("*another request*");
    }

    [Test]
    [Category("Boundary")]
    public async Task Given_AMalformedPayeeBsb_When_ThePaymentIsInitiated_Then_NothingIsWritten()
    {
        // Given
        var paymentRef = Guid.NewGuid();
        _payments.Setup(p => p.GetByRefAsync(paymentRef, It.IsAny<CancellationToken>())).ReturnsAsync((Payment?)null);
        var command = CommandFor(PaymentsTestData.NewPayment(paymentRef: paymentRef)) with { ToBsb = "08300" };

        // When
        var initiate = () => _handler.HandleAsync(command, CancellationToken.None);

        // Then
        await initiate.Should().ThrowAsync<DomainRuleViolationException>();
        _unitOfWork.Verify(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}