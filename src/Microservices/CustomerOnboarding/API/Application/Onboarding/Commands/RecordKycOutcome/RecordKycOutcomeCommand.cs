namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

/// <summary>
/// Applies a Customer KYC fact (KycCaseCreated / KycCaseApproved / KycCaseRejected)
/// to an onboarding application. MessageId is the KYC event's MessageId and is
/// the idempotency key. ApplicationNumber must match the application with
/// ApplicationId: ids are reused when a database is recreated, business numbers are not.
/// </summary>
public sealed record RecordKycOutcomeCommand(
    long ApplicationId,
    string ApplicationNumber,
    Guid MessageId,
    string EventType);

public enum RecordKycOutcomeResult
{
    /// <summary>The application changed state.</summary>
    Applied,

    /// <summary>Valid fact, but the application was already at or beyond it.</summary>
    NoChange,

    /// <summary>This message was processed before (redelivery).</summary>
    Duplicate,

    NotFound,

    /// <summary>The application with this id has a different application number (stale or misrouted fact).</summary>
    ApplicationMismatch,

    /// <summary>The event type is not a KYC outcome this context understands.</summary>
    UnsupportedEventType
}
