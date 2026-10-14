using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Monitoring;

/// <summary>How long an onboarding application may stay in one step before operations are told ("Onboarding:Alerts").</summary>
public sealed class OnboardingAlertOptions
{
    public const string SectionName = "Onboarding:Alerts";

    /// <summary>A step a machine moves on (a worker, Kafka, core banking): minutes are already too long.</summary>
    public int HandoverMinutes { get; init; }

    /// <summary>A step that waits for an officer's decision (KYC, Compliance).</summary>
    public int OfficerDecisionMinutes { get; init; }
}

// ┌─ :: ARCHITECTURE :: Onboarding applications that wait too long ────────────────────────────────
// │ Why : The choreographed onboarding has no coordinator to notice an application that stopped
// │       moving - a stopped worker, a parked message or an officer queue nobody works.
// │       Compensating on a time-out would be wrong (a timeout is not evidence), so people must be
// │       told instead.
// │ How : Readiness turns Degraded when an application has stayed in a machine step longer than
// │       HandoverMinutes, in an officer step longer than OfficerDecisionMinutes, or is in
// │       CompensationFailed; counts per step only (the endpoint is anonymous). Nothing is changed
// │       automatically. Platform Health and the /metrics health gauge show it.
// │ Ref : Features 1.7.2, 1.4.2 | Response-to-AI-Architect-Reviews (Google ARCH-02) |
// │       Saga pattern (process monitoring)
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class OnboardingAgeHealthCheck(
    CustomerDbContext db,
    IOptions<OnboardingAlertOptions> options,
    TimeProvider clock) : IHealthCheck
{
    /// <summary>Steps a machine moves on: the next hop is a subscriber, the relay or core banking.</summary>
    private static readonly OnboardingApplicationStatus[] HandoverSteps =
    [
        OnboardingApplicationStatus.Submitted,
        OnboardingApplicationStatus.KycCompleted,
        OnboardingApplicationStatus.ComplianceCompleted,
        OnboardingApplicationStatus.AccountOpeningInProgress,
        OnboardingApplicationStatus.Compensating
    ];

    /// <summary>Steps that wait for an officer's decision.</summary>
    private static readonly OnboardingApplicationStatus[] OfficerSteps =
    [
        OnboardingApplicationStatus.KycInProgress,
        OnboardingApplicationStatus.ComplianceInProgress
    ];

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var handoverCutoff = now.AddMinutes(-options.Value.HandoverMinutes);
        var officerCutoff = now.AddMinutes(-options.Value.OfficerDecisionMinutes);

        // UpdatedAt changes with every status change, so it is the moment the application entered its step.
        // One row per status (at most a dozen); which count applies is decided below.
        var perStatus = await db.OnboardingApplications.AsNoTracking()
            .GroupBy(a => a.Status)
            .Select(g => new
            {
                Status = g.Key,
                All = g.Count(),
                PastHandover = g.Count(a => a.UpdatedAt < handoverCutoff),
                PastOfficer = g.Count(a => a.UpdatedAt < officerCutoff)
            })
            .ToListAsync(cancellationToken);

        var waiting = perStatus
            .Select(s => new
            {
                s.Status,
                Count = s.Status == OnboardingApplicationStatus.CompensationFailed ? s.All
                    : HandoverSteps.Contains(s.Status) ? s.PastHandover
                    : OfficerSteps.Contains(s.Status) ? s.PastOfficer
                    : 0
            })
            .Where(w => w.Count > 0)
            .ToList();

        var data = waiting.ToDictionary(w => w.Status.ToString(), w => (object)w.Count);

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ If applications wait too long, report Degraded - never Unhealthy: the API itself
        // │ works, and taking it out of service would stop every other application too.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (waiting.Count == 0)
            return HealthCheckResult.Healthy("No onboarding application is waiting longer than expected.", data);

        var summary = string.Join(", ", waiting.OrderBy(w => w.Status).Select(w => $"{w.Count} in {w.Status}"));
        return HealthCheckResult.Degraded(
            $"Onboarding applications waiting longer than expected (machine steps > {options.Value.HandoverMinutes} min, " +
            $"officer steps > {options.Value.OfficerDecisionMinutes} min, or compensation failed): {summary}.",
            data: data);
    }
}