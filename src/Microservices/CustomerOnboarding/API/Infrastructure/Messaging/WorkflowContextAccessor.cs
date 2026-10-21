using EnterpriseWebPlatform.Common.WebUtilities.Configuration;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

// ┌─ :: ARCHITECTURE :: Workflow, correlation and causation identity ──────────────────────────────
// │ Why : One business transaction crosses many services and messages; without shared IDs nobody
// │       can follow it or tell which message caused which.
// │ How : Every request and message carries WorkflowId, CorrelationId and CausationId plus the
// │       human initiator; they are stored on Outbox rows and copied into Kafka headers.
// │ Ref : Features 1.3.4 | Correlation Identifier pattern (EIP)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Workflow metadata supplied by the caller (all optional).</summary>
public sealed record WorkflowRequestContext(
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId);

/// <summary>
/// Reads the workflow metadata and the accountable human initiator of the current
/// request. Kept out of the DbContext and the domain: it is transport (HTTP) detail.
/// </summary>
public sealed class WorkflowContextAccessor(IHttpContextAccessor httpContextAccessor, ClientIds clientIds)
{
    /// <summary>
    /// The only M2M client that may state a human initiator (X-Initiated-By-User-Id):
    /// the Onboarding Outcome Subscriber, which copies it from the KYC event (ClientIds configuration).
    /// </summary>
    private readonly string TrustedInitiatorAssertingClient = clientIds.OnboardingOutcomeSubscriber;

    public WorkflowRequestContext GetRequestContext()
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        return new WorkflowRequestContext(
            ParseGuidHeader(headers, "X-Workflow-Id"),
            ParseGuidHeader(headers, "X-Correlation-Id"),
            ParseGuidHeader(headers, "X-Causation-Id"));
    }

    public Guid? GetInitiatedByUserId()
    {
        var httpContext = httpContextAccessor.HttpContext;
        var subject = httpContext?.User.FindFirst("sub")?.Value;

        if (subject is not null)
        {
            // A human caller is the initiator.
            return Guid.TryParse(subject, out var subjectId) ? subjectId : null;
        }

        // An M2M caller has no human subject. Only the pinned Onboarding Outcome Subscriber may
        // carry the ORIGINAL human initiator forward (it copies it from the KYC
        // event), so the workflow's accountability survives the asynchronous hop.
        // It is attribution only - never an authorization grant.
        var clientId = httpContext?.User.FindFirst("client_id")?.Value;

        if (string.Equals(clientId, TrustedInitiatorAssertingClient, StringComparison.Ordinal) &&
            Guid.TryParse(httpContext!.Request.Headers["X-Initiated-By-User-Id"].FirstOrDefault(), out var initiator))
        {
            return initiator;
        }

        return null;
    }

    private static Guid? ParseGuidHeader(IHeaderDictionary? headers, string name) =>
        headers is not null &&
        headers.TryGetValue(name, out var value) &&
        Guid.TryParse(value.FirstOrDefault(), out var parsed)
            ? parsed
            : null;
}