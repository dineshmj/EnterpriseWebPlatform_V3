using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.DocumentsManagement.DocumentInvalidationSubscriber.Processing;

/// <summary>
/// Keeps Documents Management's evidence in step with the onboarding saga:
///  - onboarding.application.submitted → "attach these evidence documents" (retained,
///    no longer deletable);
///  - onboarding.application.rejected  → "invalidate these evidence documents"
///    (saga compensation; retained, never deleted).
/// Results are classified as:
///  - Processed   : applied, already applied, or nothing to do (200);
///  - Dead letter : malformed, unknown event type, required fields missing, or 4xx;
///  - Transient   : 401/403/408/429/5xx, timeouts, open circuit - retried in place.
/// The worker holds no document rules: the Document aggregate does.
/// </summary>
public sealed class DocumentEvidenceProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<DocumentEvidenceProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "DocumentsManagementApi";

    private const string SubmittedEventType = "OnboardingApplicationSubmitted";
    private const string RejectedEventType = "OnboardingApplicationRejected";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ProcessingOutcome> ProcessAsync(ConsumedMessage consumed, CancellationToken cancellationToken)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(consumed.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ProcessingOutcome.ToDeadLetter($"Malformed JSON: {ex.Message}");
        }

        if (envelope is null || envelope.MessageId == Guid.Empty)
            return ProcessingOutcome.ToDeadLetter("Message has no MessageId.");

        var rejected = string.Equals(envelope.EventType, RejectedEventType, StringComparison.Ordinal);
        if (!rejected && !string.Equals(envelope.EventType, SubmittedEventType, StringComparison.Ordinal))
            return ProcessingOutcome.ToDeadLetter($"Unexpected event type '{envelope.EventType}'; expected {SubmittedEventType} or {RejectedEventType}.");

        var p = envelope.Payload;
        if (p is null || p.ApplicationRef == Guid.Empty ||
            string.IsNullOrWhiteSpace(p.ApplicationNumber) || string.IsNullOrWhiteSpace(p.BranchCode) ||
            (rejected && string.IsNullOrWhiteSpace(p.RejectedBy)))
        {
            return ProcessingOutcome.ToDeadLetter(rejected
                ? "Event does not contain ApplicationRef, ApplicationNumber, BranchCode and RejectedBy."
                : "Event does not contain ApplicationRef, ApplicationNumber and BranchCode.");
        }

        var documentIds = (p.EvidenceDocuments ?? [])
            .Select(x => x.DocumentId)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        logger.LogInformation(
            "Received {EventType} MessageId={MessageId} ApplicationRef={ApplicationRef} ({ApplicationNumber}): {Count} evidence document(s).",
            envelope.EventType, envelope.MessageId, p.ApplicationRef, p.ApplicationNumber, documentIds.Count);

        object body = rejected
            ? new
            {
                messageId = envelope.MessageId,
                applicationRef = p.ApplicationRef,
                applicationNumber = p.ApplicationNumber,
                branchCode = p.BranchCode,
                rejectedBy = p.RejectedBy,
                documentIds
            }
            : new
            {
                messageId = envelope.MessageId,
                applicationRef = p.ApplicationRef,
                applicationNumber = p.ApplicationNumber,
                branchCode = p.BranchCode,
                documentIds
            };
        var path = rejected ? "/internal/v1/documents/invalidations" : "/internal/v1/documents/attachments";

        // Still sent with no documents: DM records the message in its Inbox, so the
        // outcome is visible and never re-applied.
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(path, body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                tokenClient.Invalidate();
                response = await SendAsync(path, body, cancellationToken);
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new TransientProcessingException($"Documents Management API unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "{Action} the evidence of application {ApplicationNumber}: {Response}",
                    rejected ? "Invalidated" : "Attached", p.ApplicationNumber, responseBody);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403: this worker's identity is not accepted - a configuration problem, not a bad message.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
                throw new TransientProcessingException($"Documents Management API returned HTTP {status} after the resilience pipeline: {responseBody}");

            return ProcessingOutcome.ToDeadLetter($"Documents Management API rejected the message with HTTP {status}: {responseBody}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string path, object body, CancellationToken cancellationToken)
    {
        var token = await tokenClient.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
    }

    // Tolerant reader: only the fields Documents Management needs; unknown fields are ignored.
    private sealed record Envelope(
        Guid MessageId,
        string? EventType,
        Payload? Payload);

    private sealed record Payload(
        Guid ApplicationRef,
        string? ApplicationNumber,
        string? BranchCode,
        string? RejectedBy,
        IReadOnlyList<EvidenceDocument>? EvidenceDocuments);

    private sealed record EvidenceDocument(Guid DocumentId, string? DocumentType);
}