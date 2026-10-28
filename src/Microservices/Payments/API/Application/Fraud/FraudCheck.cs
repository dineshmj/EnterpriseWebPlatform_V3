using System.Diagnostics;
using System.Diagnostics.Metrics;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Application.Fraud;

/// <summary>
/// The fraud check's limits (configuration Payments:FraudCheck): how long the staff member's
/// request may wait for a score, and from which amount the fallback rules ask for a review.
/// </summary>
public sealed record FraudCheckPolicy(TimeSpan Budget, decimal FallbackReviewAmount)
{
    public static readonly TimeSpan MinBudget = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan MaxBudget = TimeSpan.FromSeconds(2);
}

/// <summary>
/// What this API decides alone when the fraud scoring gives no usable answer in time: it knows
/// only the amount and whether the customer paid this payee before.
/// </summary>
public static class FraudFallbackRules
{
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string NewPayee = "NEW_PAYEE";
    public const string AmountAtOrAboveFallbackLimit = "AMOUNT_AT_OR_ABOVE_FALLBACK_LIMIT";

    // ┌─ :: SECURITY + RESILIENCE :: Fallback rules: cautious, never a blind "allow" ──────────────────
    // │ Why : A late or absent score must neither stop payments (an outage of one provider would
    // │       stop the bank) nor wave a risky one through unseen.
    // │ How : A payee this customer never paid, or an amount at or above the fallback limit, goes
    // │       to a payments officer (REVIEW); anything else follows its tier (ALLOW). Never DECLINE
    // │       - that needs the provider's evidence. The payment records that the fallback decided.
    // │ Ref : Payments API README (fraud check) | Release It! (fallback) | OWASP API6
    // └────────────────────────────────────────────────────────────────────────────────────────────────
    public static FraudAssessment Assess(decimal amount, bool firstPaymentToPayee, decimal fallbackReviewAmount)
    {
        var reasons = new List<string> { ProviderUnavailable };
        if (firstPaymentToPayee)
            reasons.Add(NewPayee);
        if (amount >= fallbackReviewAmount)
            reasons.Add(AmountAtOrAboveFallbackLimit);

        return FraudAssessment.FromFallback(reasons.Count > 1 ? FraudDecision.Review : FraudDecision.Allow, reasons);
    }
}

// ┌─ :: ARCHITECTURE + RESILIENCE :: A fraud check within a latency budget ────────────────────────
// │ Why : The staff member waits for the payment to be accepted; a slow fraud provider must not
// │       make every payment slow - nor may its outage stop payments or let them through unseen.
// │ How : The provider is asked ONCE, with the budget as a hard deadline (the HTTP pipeline's
// │       timeout, and this deadline again around it; a retry would not fit in the budget). No
// │       usable answer in time -> the fallback rules. Every check is counted and timed by
// │       decision and source (ewp_payments_fraud_checks_total, ewp_payments_fraud_check_duration),
// │       so a dashboard shows how often the fallback decides and how close the provider runs to
// │       the budget.
// │ Ref : Payments API README (fraud check) | Features 1.4 | SRE latency budgets
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Asks the fraud scoring within the budget; falls back to <see cref="FraudFallbackRules"/>.</summary>
public sealed class FraudCheck(
    IFraudScoring scoring,
    IPaymentRepository payments,
    FraudCheckPolicy policy,
    IMeterFactory meterFactory,
    ILogger<FraudCheck> logger)
{
    public const string MeterName = "EnterpriseWebPlatform.Payments.FraudCheck";

    private readonly Counter<long> checks = meterFactory.Create(MeterName).CreateCounter<long>(
        "ewp.payments.fraud_checks", description: "Fraud checks at payment initiation, by decision and by who decided (provider / fallback).");

    private readonly Histogram<double> duration = meterFactory.Create(MeterName).CreateHistogram<double>(
        "ewp.payments.fraud_check.duration", unit: "s", description: "Time spent asking the fraud scoring, against the latency budget.");

    public async Task<FraudAssessment> AssessAsync(FraudScoringRequestDraft draft, CancellationToken cancellationToken)
    {
        var firstPaymentToPayee = !await payments.HasPaidBeforeAsync(draft.CustomerNumber, draft.To, cancellationToken);
        var request = new FraudScoringRequest(
            draft.PaymentRef, draft.PaymentNumber, draft.CustomerNumber, draft.From.Bsb, draft.From.AccountNumber,
            draft.PayeeName, draft.To.Bsb, draft.To.AccountNumber, draft.Amount, PaymentRules.Aud, firstPaymentToPayee);

        var started = Stopwatch.GetTimestamp();
        FraudAssessment assessment;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(policy.Budget);
            var result = await scoring.ScoreAsync(request, deadline.Token);
            assessment = FraudAssessment.FromProvider(result.Decision, result.Score, result.Reasons);
        }
        catch (Exception ex) when (ex is FraudScoringUnavailableException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
            // │ The fallback decided: a warning with the reason, so an alert can count them.
            // └──────────────────────────────────────────────────────────────────────────────────────
            assessment = FraudFallbackRules.Assess(draft.Amount, firstPaymentToPayee, policy.FallbackReviewAmount);
            logger.LogWarning("Fraud check for {PaymentNumber}: no usable score within {Budget} ms ({Reason}); the fallback rules decided {Decision}.",
                draft.PaymentNumber, policy.Budget.TotalMilliseconds, ex is OperationCanceledException ? "the budget ran out" : ex.Message, assessment.Decision.ToCode());
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var tags = new TagList { { "decision", assessment.Decision.ToCode().ToLowerInvariant() }, { "source", assessment.Source.ToCode().ToLowerInvariant() } };
        checks.Add(1, tags);
        duration.Record(elapsed.TotalSeconds, tags);
        logger.LogInformation("Fraud check for {PaymentNumber}: {Decision} by {Source} (score {Score}) in {ElapsedMs} ms.",
            draft.PaymentNumber, assessment.Decision.ToCode(), assessment.Source.ToCode(), assessment.Score, (int)elapsed.TotalMilliseconds);
        return assessment;
    }
}

/// <summary>The payment as captured, before it exists: what the fraud check needs.</summary>
public sealed record FraudScoringRequestDraft(
    Guid PaymentRef,
    string PaymentNumber,
    string CustomerNumber,
    BankAccountRef From,
    string PayeeName,
    BankAccountRef To,
    decimal Amount);