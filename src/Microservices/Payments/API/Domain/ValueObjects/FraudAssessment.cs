using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

/// <summary>What the fraud check says about a payment about to be made.</summary>
public enum FraudDecision
{
    /// <summary>Nothing unusual: the payment follows its tier.</summary>
    Allow = 1,

    /// <summary>A payments officer must approve it, whatever the amount.</summary>
    Review = 2,

    /// <summary>Not made: the payment ends REJECTED before any funds are reserved.</summary>
    Decline = 3
}

/// <summary>Who decided: the payment network's fraud scoring, or this API's own rules when it could not answer in time.</summary>
public enum FraudSource
{
    Provider = 1,
    Fallback = 2
}

// ┌─ :: ARCHITECTURE + SECURITY :: The fraud check's answer, as the domain keeps it ───────────────
// │ Why : A payment's risk decision is evidence: who decided (the provider or the fallback), on
// │       what score and why - and a fallback, which knows little, must never decline a payment
// │       nobody suspected.
// │ How : A value object made only through FromProvider (score 0-999 required) or FromFallback
// │       (no score; Allow or Review only). Stored on the payment with the reasons.
// │ Ref : Payments API README (fraud check) | Blueprint "Fraud and risk" | OWASP API6
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The fraud check's answer for one payment: the decision, who made it, the provider's score
/// (0-999; none for the fallback) and the reasons (short codes, for the officer and the audit).
/// </summary>
public sealed record FraudAssessment
{
    public const int MaxScore = 999;
    private const int MaxReasonsLength = 200;

    private FraudAssessment(FraudDecision decision, FraudSource source, int? score, string? reasons)
    {
        Decision = decision;
        Source = source;
        Score = score;
        Reasons = reasons;
    }

    public FraudDecision Decision { get; }

    public FraudSource Source { get; }

    public int? Score { get; }

    /// <summary>Comma-separated reason codes, e.g. "NEW_PAYEE, HIGH_AMOUNT"; null when there are none.</summary>
    public string? Reasons { get; }

    public static FraudAssessment FromProvider(FraudDecision decision, int score, IEnumerable<string> reasons)
    {
        if (score is < 0 or > MaxScore)
            throw new DomainRuleViolationException($"A fraud score is 0 to {MaxScore}; {score} is not one.");
        return new FraudAssessment(decision, FraudSource.Provider, score, Join(reasons));
    }

    public static FraudAssessment FromFallback(FraudDecision decision, IEnumerable<string> reasons)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The fallback knows only the amount and whether the payee is new: it may send a payment
        // │ to an officer, never decline it - a decline needs the provider's evidence.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (decision == FraudDecision.Decline)
            throw new DomainRuleViolationException("The fallback rules cannot decline a payment; only the fraud provider can.");
        return new FraudAssessment(decision, FraudSource.Fallback, null, Join(reasons));
    }

    /// <summary>Rebuilds a stored assessment (persistence only).</summary>
    internal static FraudAssessment Restore(FraudDecision decision, FraudSource source, int? score, string? reasons) =>
        new(decision, source, score, reasons);

    /// <summary>One line for the payment's timeline.</summary>
    public string Describe() =>
        (Source == FraudSource.Provider ? $"the payment network's fraud scoring, score {Score}" : "the fallback rules (the fraud scoring did not answer in time)") +
        $": {Decision.ToCode()}" + (Reasons is null ? "." : $" ({Reasons}).");

    private static string? Join(IEnumerable<string> reasons)
    {
        var text = string.Join(", ", reasons.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal));
        return text.Length == 0 ? null : text.Length > MaxReasonsLength ? text[..MaxReasonsLength] : text;
    }
}