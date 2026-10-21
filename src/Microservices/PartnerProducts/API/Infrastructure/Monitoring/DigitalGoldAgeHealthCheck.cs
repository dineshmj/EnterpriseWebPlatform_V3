using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.PartnerProducts.Api.Domain;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure.Monitoring;

/// <summary>How long a Form VII or a purchase may stay in one step before operations are told ("DigitalGold:Alerts").</summary>
public sealed class DigitalGoldAlertOptions
{
    public const string SectionName = "DigitalGold:Alerts";

    /// <summary>
    /// The Partner Products Worker cancels an order without a payment after 30 minutes, at its next
    /// look - at most 5 minutes later (Worker workflows.ts): by then it is cancelled.
    /// </summary>
    public const int WorkerCancelsUnpaidWithinMinutes = 35;

    /// <summary>A step that needs no person: the Partner Bank's answer to a sent Form VII or slip, the payment advice after the payment.</summary>
    public int HandoverMinutes { get; init; }

    /// <summary>An order without a payment: past the Worker's own cancellation, so more than <see cref="WorkerCancelsUnpaidWithinMinutes"/>.</summary>
    public int UnpaidOrderMinutes { get; init; }

    /// <summary>A started payment that is not final yet (approval, the payment network).</summary>
    public int PaymentDecisionMinutes { get; init; }
}

// ┌─ :: ARCHITECTURE :: Form VII applications and purchases that wait too long ────────────────────
// │ Why : A Form VII or a purchase is moved on by the Partner Bank and, for a purchase, the Temporal
// │       workflow. If the Worker or Temporal stopped, the Partner Bank cannot be reached or a
// │       payment waits in a queue nobody works, nothing on the screen says so - the customer's
// │       account opening, order or money just waits.
// │ How : Readiness turns Degraded (counts only - the endpoint is anonymous) when a Form VII is SENT
// │       without an answer for HandoverMinutes, or a purchase is SENT without an answer or PAID
// │       without the advice delivered for HandoverMinutes, ORDERED without a payment for
// │       UnpaidOrderMinutes (the Worker cancels it within 35), ORDERED with a payment that is not
// │       final for PaymentDecisionMinutes, or carries a problem only a person can settle (status
// │       reason on an open purchase). Nothing is changed automatically. Platform Health and the
// │       /metrics health gauge show it.
// │ Ref : Features 1.7.2, 1.4.4 | OnboardingAgeHealthCheck (the same idea for onboarding) |
// │       Response-to-AI-Architect-Reviews round 4 (Microsoft: stuck-purchase check)
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class DigitalGoldAgeHealthCheck(
    PartnerProductsDbContext db,
    IOptions<DigitalGoldAlertOptions> options,
    TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var alerts = options.Value;
        var now = clock.GetUtcNow();
        var handoverCutoff = now.AddMinutes(-alerts.HandoverMinutes);
        var unpaidCutoff = now.AddMinutes(-alerts.UnpaidOrderMinutes);
        var paymentCutoff = now.AddMinutes(-alerts.PaymentDecisionMinutes);

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ A Form VII waits only while SENT (no answer yet - the counter sends again); RECORDED
        // │ waits for the counter, OPENED / REFUSED are final. SentAt is the FIRST send: sending
        // │ again does not restart the clock.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var applicationSentUnanswered = await db.Applications.AsNoTracking()
            .CountAsync(a => a.Status == GoldAccountApplication.Sent && a.SentAt < handoverCutoff, cancellationToken);

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Open purchases only (SENT, ORDERED, PAID): RECORDED waits for the counter and ends
        // │ with the slip's day; the rest are final. A purchase with a status reason is counted
        // │ once, as needing a person. For an ORDERED purchase without a payment and without a
        // │ reason, UpdatedAt is the moment it was ordered (nothing else touches it meanwhile).
        // └──────────────────────────────────────────────────────────────────────────────────────
        var purchases = await db.Purchases.AsNoTracking()
            .Where(p => p.Status == GoldPurchase.Sent || p.Status == GoldPurchase.Ordered || p.Status == GoldPurchase.Paid)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                NeedsAttention = g.Count(p => p.StatusReason != null),
                SentUnanswered = g.Count(p => p.StatusReason == null && p.Status == GoldPurchase.Sent && p.SentAt < handoverCutoff),
                OrderedUnpaid = g.Count(p => p.StatusReason == null && p.Status == GoldPurchase.Ordered && p.PaymentNumber == null && p.UpdatedAt < unpaidCutoff),
                PaymentNotFinal = g.Count(p => p.StatusReason == null && p.Status == GoldPurchase.Ordered && p.PaymentNumber != null && p.PaymentStartedAt < paymentCutoff),
                PaidAdviceOutstanding = g.Count(p => p.StatusReason == null && p.Status == GoldPurchase.Paid && p.PaidAt < handoverCutoff)
            })
            .SingleOrDefaultAsync(cancellationToken);

        var waiting = new Dictionary<string, object>();
        void Add(string name, int count) { if (count > 0) waiting[name] = count; }
        Add("ApplicationSentUnanswered", applicationSentUnanswered);
        if (purchases is not null)
        {
            Add("PurchaseNeedsAttention", purchases.NeedsAttention);
            Add("PurchaseSentUnanswered", purchases.SentUnanswered);
            Add("PurchaseOrderedUnpaid", purchases.OrderedUnpaid);
            Add("PurchasePaymentNotFinal", purchases.PaymentNotFinal);
            Add("PurchasePaidAdviceOutstanding", purchases.PaidAdviceOutstanding);
        }

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ If anything waits too long, report Degraded - never Unhealthy: the API itself works,
        // │ and taking it out of service would stop every other Form VII and purchase too.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (waiting.Count == 0)
            return HealthCheckResult.Healthy("No Form VII or purchase is waiting longer than expected.", waiting);

        var summary = string.Join(", ", waiting.Select(w => $"{w.Value} {w.Key}"));
        return HealthCheckResult.Degraded(
            $"Digital Gold waiting longer than expected (no answer or advice > {alerts.HandoverMinutes} min, " +
            $"unpaid order > {alerts.UnpaidOrderMinutes} min, payment not final > {alerts.PaymentDecisionMinutes} min, " +
            $"or a problem for a person): {summary}.",
            data: waiting);
    }
}