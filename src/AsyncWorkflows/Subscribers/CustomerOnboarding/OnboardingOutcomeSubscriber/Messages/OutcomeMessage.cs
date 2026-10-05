using System.Text.Json;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.OnboardingOutcomeSubscriber.Messages;

/// <summary>
/// Tolerant-reader view of the KYC, Compliance and Accounts events (kyc.case.*,
/// compliance.case.* and accounts.*): only the fields Customer Onboarding needs. Unknown fields
/// are ignored, so producers can add fields without breaking this consumer.
/// KycCaseId carries the producing case's ID (KycCaseId, ComplianceCaseId or AccountApplicationId), for logs.
/// </summary>
public sealed record OutcomeMessage(
    Guid MessageId,
    string EventType,
    long KycCaseId,
    Guid ApplicationRef,
    string? ApplicationNumber,
    Guid? WorkflowId,
    Guid? CorrelationId,
    string? InitiatedByUserId)
{
    /// <summary>
    /// Reads the standard envelope (workflow metadata at the top level, the event
    /// under "Payload"). Messages published before KYC adopted the envelope carried
    /// every field at the top level; they are still accepted, so no topic reset is needed.
    /// </summary>
    public static OutcomeMessage? Parse(string json, JsonSerializerOptions options)
    {
        var wire = JsonSerializer.Deserialize<Wire>(json, options);
        if (wire is null)
            return null;

        var body = wire.Payload;
        return new OutcomeMessage(
            wire.MessageId,
            wire.EventType ?? string.Empty,
            body?.KycCaseId ?? body?.ComplianceCaseId ?? body?.AccountApplicationId ?? wire.KycCaseId ?? 0,
            body?.ApplicationRef ?? wire.ApplicationRef ?? Guid.Empty,
            body?.ApplicationNumber ?? wire.ApplicationNumber,
            wire.WorkflowId,
            wire.CorrelationId,
            wire.InitiatedByUserId);
    }

    private sealed record Wire(
        Guid MessageId,
        string? EventType,
        Guid? WorkflowId,
        Guid? CorrelationId,
        string? InitiatedByUserId,
        Body? Payload,
        // Pre-envelope (flat) shape.
        long? KycCaseId,
        Guid? ApplicationRef,
        string? ApplicationNumber);

    private sealed record Body(
        long? KycCaseId,
        long? ComplianceCaseId,
        long? AccountApplicationId,
        Guid? ApplicationRef,
        string? ApplicationNumber);
}