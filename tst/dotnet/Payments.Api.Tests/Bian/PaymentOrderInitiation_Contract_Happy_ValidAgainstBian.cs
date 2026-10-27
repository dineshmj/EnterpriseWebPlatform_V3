using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Application.Queries;
using EnterpriseWebPlatform.Payments.Api.Bian;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;
using EnterpriseWebPlatform.Tests.Bian;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Bian;

// ┌─ :: ARCHITECTURE :: /v2 is BIAN only if BIAN's own file says so ───────────────────────────────
// │ Why : The /v2 contract claims BIAN PaymentOrderInitiation (release 14.0.0). A renamed property,
// │       a wrong nesting or a status value BIAN does not list would break that claim silently.
// │ How : Every body /v2 sends or accepts is validated against BIAN's published OpenAPI file
// │       (doc/bian, pinned) - for every EWP payment status, since each maps to a BIAN status type.
// │ Ref : doc/bian/README.md | Payments API README (/v2)
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Happy")]
public sealed class PaymentOrderInitiation_Contract_Happy_ValidAgainstBian
{
    private const string BianFile = "PaymentOrderInitiation.yaml";

    public static IEnumerable<string> EveryPaymentStatus() =>
        Enum.GetValues<PaymentStatus>().Select(s => s.ToCode());

    [TestCaseSource(nameof(EveryPaymentStatus))]
    public void Given_APaymentInAnyStatus_When_RetrievedThroughV2_Then_TheBodyIsValidAgainstBiansSchema(string status)
    {
        // Given
        var payment = Payment(status);

        // When
        var transaction = PaymentOrderInitiationMapper.ToTransaction(payment);

        // Then
        BianSchema.Validate(BianFile, "PaymentOrderInitiationTransaction", transaction, BianJson.Options)
            .Should().BeEmpty();
        transaction.PaymentTransaction!.PaymentTransactionStatus!.PaymentTransactionStatus!.StatusReason
            .Should().Be(status, "EWP's own status always travels with BIAN's coarser one");
    }

    [Test]
    public void Given_ARequestAsAPartnerWouldSendIt_When_CheckedAgainstBian_Then_ItIsValidAndMapsToAPayment()
    {
        // Given
        var request = PaymentOrderInitiation_ToPaymentOrder_Edge_RequiredFields.FullRequest();

        // When
        var errors = BianSchema.Validate(BianFile, "PaymentOrderInitiationTransaction", request, BianJson.Options);
        var (order, mappingErrors) = PaymentOrderInitiationMapper.ToPaymentOrder(request);

        // Then
        errors.Should().BeEmpty();
        mappingErrors.Should().BeEmpty();
        order.Should().NotBeNull();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Given_ADecision_When_AnsweredThroughTheExchange_Then_TheBodyIsValidAgainstBiansSchema(bool approved)
    {
        // Given / When
        var answer = PaymentOrderInitiationMapper.ToOrderInitiation("PAY-20261015-000042", approved ? "SENDING_TO_NETWORK" : "COMPENSATING", approved);

        // Then
        BianSchema.Validate(BianFile, "OrderInitiation", answer, BianJson.Options).Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Given_ABodyWithAValueBianDoesNotList_When_Validated_Then_TheCheckFindsIt(bool nested)
    {
        // Given - the check itself must be able to fail, or a green test proves nothing; nested:
        // three $refs deep (PaymentTransaction -> Paymenttransactionstatus -> its status type enum)
        var valid = PaymentOrderInitiationMapper.ToTransaction(Payment("COMPLETED"));
        var transaction = nested
            ? valid with
            {
                PaymentTransaction = valid.PaymentTransaction! with
                {
                    PaymentTransactionStatus = valid.PaymentTransaction.PaymentTransactionStatus! with { PaymentTransactionStatusType = "Done" }
                }
            }
            : valid with { PaymentMechanism = "CARRIER_PIGEON" };

        // When
        var errors = BianSchema.Validate(BianFile, "PaymentOrderInitiationTransaction", transaction, BianJson.Options);

        // Then
        errors.Should().NotBeEmpty();
    }

    internal static PaymentDetail Payment(string status) => new(
        PaymentId: 42,
        PaymentRef: Guid.Parse("5b0e8a1c-7d2f-4a44-9a51-2a8d3b6c1e90"),
        PaymentNumber: "PAY-20261015-000042",
        CustomerNumber: "CUS-000123",
        FromBsb: "062-000",
        FromAccountNumber: "72947086",
        PayeeName: "Harbour Plumbing",
        ToBsb: "083-004",
        ToAccountNumber: "12345678",
        Amount: 1100m,
        Currency: "AUD",
        Reference: "Invoice 7781",
        BranchCode: "SYD001",
        Status: status,
        ApprovalRequired: true,
        OutcomeCode: null,
        OutcomeReason: null,
        NetworkReference: null,
        CreatedAt: PaymentsTestData.Now,
        UpdatedAt: PaymentsTestData.Now.AddMinutes(3),
        EndedAt: null,
        InitiatedByUserId: "sophie",
        InitiatedByLanId: "sophie.k",
        Saga: null,
        DecisionByUserId: null,
        DecisionByLanId: null,
        DecisionAt: null,
        DecisionRemarks: null);
}