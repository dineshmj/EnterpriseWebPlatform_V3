using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Support;

/// <summary>
/// Known people, money and settings for the Payments domain tests, and short ways to bring a
/// payment and its saga to a given step - through the aggregates' own public methods only.
/// </summary>
internal static class PaymentsTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public const string Sophie = "sub-sophie";   // customer service: initiates
    public const string Emily = "sub-emily";     // payments officer: approves
    public const string Daniel = "sub-daniel";   // operations: proposes a resolution
    public const string Michael = "sub-michael"; // platform administrator: decides it

    public const decimal ApprovalThreshold = 1_000m;

    public static readonly BranchCode Sydney = BranchCode.Create("SYD001");

    /// <summary>The Development settings: replies 30 s doubling to 5 min, 3 commands, network 5 s doubling to 60 s, 4 sends, alert after 2 min.</summary>
    public static readonly SagaPolicy Policy = new(
        ReplyTimeout: TimeSpan.FromSeconds(30),
        MaxReplyTimeout: TimeSpan.FromMinutes(5),
        MaxCommandAttempts: 3,
        NetworkRetryInitialDelay: TimeSpan.FromSeconds(5),
        NetworkRetryMaxDelay: TimeSpan.FromSeconds(60),
        MaxNetworkAttempts: 4,
        UnconfirmedAlertAfter: TimeSpan.FromMinutes(2));

    /// <summary>Clearance 3: up to 10,000; 4: up to 100,000; 5: no limit; below 3: none.</summary>
    public static readonly ApprovalLimits Limits = new(new Dictionary<int, decimal?>
    {
        [3] = 10_000m,
        [4] = 100_000m,
        [5] = null
    });

    public static BankAccountRef Payer => BankAccountRef.Create("062-000", "72947086", "paying");

    public static BankAccountRef Payee => BankAccountRef.Create("083-004", "12345678", "payee");

    /// <summary>The fraud check's usual answer: the provider allows the payment (score 120).</summary>
    public static FraudAssessment Allowed => FraudAssessment.FromProvider(FraudDecision.Allow, 120, []);

    /// <summary>A payment as the API saves it (with its database ID), started by Sophie.</summary>
    public static Payment NewPayment(decimal amount = 140m, string initiatedBy = Sophie, Guid? paymentRef = null, FraudAssessment? fraud = null)
    {
        var payment = Payment.Initiate(
            paymentRef ?? Guid.NewGuid(), "PAY-261015-TEST01", "CUST-100001", Payer, "Harbour Plumbing Pty Ltd", Payee,
            amount, "Invoice 42", Sydney, initiatedBy, ApprovalThreshold, fraud ?? Allowed, Now);
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ The database assigns the ID (a saga only starts for a saved payment): set it the way
        // │ EF Core does, through the private setter.
        // └──────────────────────────────────────────────────────────────────────────────────────
        typeof(Payment).GetProperty(nameof(Payment.Id))!.SetValue(payment, 101L);
        return payment;
    }

    /// <summary>A started saga: ReserveFunds sent, waiting for Accounts' reply.</summary>
    public static (Payment Payment, PaymentSaga Saga) Started(decimal amount = 140m)
    {
        var payment = NewPayment(amount);
        var saga = PaymentSaga.Start(payment, Policy, requestId: payment.PaymentRef, Now);
        return (payment, saga);
    }

    /// <summary>Funds reserved: within the tier the saga sends to the network; above it, it waits for an officer.</summary>
    public static (Payment Payment, PaymentSaga Saga) FundsReserved(decimal amount = 140m)
    {
        var (payment, saga) = Started(amount);
        saga.OnFundsReserved(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, Now);
        return (payment, saga);
    }

    /// <summary>Every send failed: the outcome is unknown and the saga asks the network.</summary>
    public static (Payment Payment, PaymentSaga Saga) ConfirmingWithNetwork()
    {
        var (payment, saga) = FundsReserved();
        for (var attempt = 1; attempt <= Policy.MaxNetworkAttempts; attempt++)
            saga.OnNetworkUnavailable(payment, "503 Service Unavailable", Policy, Now);
        return (payment, saga);
    }

    /// <summary>Unconfirmed for longer than the alert time: people may now reconcile it.</summary>
    public static (Payment Payment, PaymentSaga Saga, DateTimeOffset Later) UnconfirmedTooLong()
    {
        var (payment, saga) = ConfirmingWithNetwork();
        return (payment, saga, Now + Policy.UnconfirmedAlertAfter + TimeSpan.FromSeconds(1));
    }

    /// <summary>The funds commands the saga issued to Accounts, in order.</summary>
    public static IReadOnlyList<FundsCommandIssuedDomainEvent> FundsCommands(this PaymentSaga saga) =>
        saga.DomainEvents.OfType<FundsCommandIssuedDomainEvent>().ToList();

    /// <summary>The kinds of the saga's history entries, in order.</summary>
    public static IReadOnlyList<string> HistoryKinds(this PaymentSaga saga) =>
        saga.History.Select(h => h.Kind).ToList();
}