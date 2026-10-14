using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.Saga;

// ┌─ :: ARCHITECTURE :: The saga's timer ──────────────────────────────────────────────────────────
// │ Why : Time-outs and due network calls need a clock that survives restarts and runs on several
// │       instances.
// │ How : A background loop picks due sagas with FOR UPDATE SKIP LOCKED (one instance per saga),
// │       hands each result to the saga, and stops a round when the network is failing; readiness
// │       degrades for stuck, overdue or unconfirmed payments.
// │ Ref : Features 1.4.3, 1.7.2 | Saga pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The saga's timer, in the Payments API: every second it runs the steps that are due -
/// a payment to send to the network, or a reply from Accounts that did not come in time -
/// one saga at a time, until none is left. It decides nothing itself: each result goes to
/// the saga. Several API instances share the work safely (SKIP LOCKED in the repository).
/// </summary>
public sealed class SagaStepRunner(
    IServiceScopeFactory scopeFactory,
    [FromKeyedServices(SagaStepRunner.HeartbeatKey)] LoopHeartbeat heartbeat,
    ILogger<SagaStepRunner> logger) : BackgroundService
{
    public const string HeartbeatKey = "payments-saga-step-runner";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                for (var i = 0; i < 50; i++)
                {
                    using var scope = scopeFactory.CreateScope();
                    var outcome = await scope.ServiceProvider.GetRequiredService<RunDueSagaStepCommandHandler>().HandleAsync(stoppingToken);

                    // Stop this round when nothing is due, or the network is failing: the other
                    // sagas keep their own retry times.
                    if (outcome is SagaStepRunOutcome.NothingDue or SagaStepRunOutcome.NetworkUnavailable or SagaStepRunOutcome.NetworkStatusUnknown)
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Payment saga step run failed."); }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

/// <summary>
/// Readiness signal: Degraded (never Unhealthy) while payments are stuck - a compensation
/// failed (operations must act), a running saga is long overdue (the step runner or a
/// participant is not keeping up), or the payment network has not confirmed a payment for
/// longer than Payments:Saga:UnconfirmedAlertMinutes (funds held, outcome unknown: operations
/// must reconcile). The API itself is fine, so this must not take it out of service.
/// </summary>
public sealed class SagaBacklogHealthCheck(PaymentsDbContext db, SagaPolicy policy, TimeProvider time) : IHealthCheck
{
    private static readonly TimeSpan Overdue = TimeSpan.FromMinutes(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var cutoff = now - Overdue;
        var unconfirmedCutoff = now - policy.UnconfirmedAlertAfter;
        var stuck = await db.PaymentSagas.AsNoTracking().CountAsync(x => x.Status == SagaStatus.Stuck, cancellationToken);
        var overdue = await db.PaymentSagas.AsNoTracking()
            .CountAsync(x => x.Status == SagaStatus.Running && x.NextCheckAt < cutoff, cancellationToken);
        var unconfirmed = await db.PaymentSagas.AsNoTracking()
            .CountAsync(x => x.Status == SagaStatus.Running && x.UnconfirmedSince <= unconfirmedCutoff, cancellationToken);
        var running = await db.PaymentSagas.AsNoTracking().CountAsync(x => x.Status == SagaStatus.Running, cancellationToken);

        var data = new Dictionary<string, object>
        {
            ["running"] = running, ["overdue"] = overdue, ["compensationFailed"] = stuck, ["unconfirmedTooLong"] = unconfirmed
        };
        if (stuck == 0 && overdue == 0 && unconfirmed == 0)
            return HealthCheckResult.Healthy($"{running} payment saga(s) running.", data);

        return HealthCheckResult.Degraded(
            $"{stuck} payment(s) with a failed compensation (operations must retry the release); {overdue} saga(s) overdue by more than {Overdue.TotalMinutes:F0} min; " +
            $"{unconfirmed} payment(s) unconfirmed by the network for more than {policy.UnconfirmedAlertAfter.TotalMinutes:F0} min (operations must reconcile).",
            data: data);
    }
}