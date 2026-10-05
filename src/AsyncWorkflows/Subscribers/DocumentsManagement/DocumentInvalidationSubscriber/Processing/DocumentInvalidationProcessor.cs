using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.DocumentsManagement.DocumentInvalidationSubscriber.Processing;

/// <summary>
/// Saga compensation: turns one onboarding.application.rejected event into "invalidate
/// these evidence documents" on the Documents Management API, and classifies the result:
///  - Processed   : invalidated, already invalidated, or nothing to do (200);
///  - Dead letter : malformed, wrong event type, required fields missing, or 4xx;
///  - Transient   : 401/403/408/429/5xx, timeouts, open circuit - retried in place.
/// The worker holds no document rules: the Document aggregate does (retain, never delete).
/// </summary>
public sealed class DocumentInvalidationProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<DocumentInvalidationProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "DocumentsManagementApi";

    private const string ExpectedEventType = "OnboardingApplicationRejected";

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
        if (!string.Equals(envelope.EventType, ExpectedEventType, StringComparison.Ordinal))
            return ProcessingOutcome.ToDeadLetter($"Unexpected event type '{envelope.EventType}'; expected {ExpectedEventType}.");

        var p = envelope.Payload;
        if (p is null || p.ApplicationRef == Guid.Empty ||
            string.IsNullOrWhiteSpace(p.ApplicationNumber) || string.IsNullOrWhiteSpace(p.BranchCode) ||
            string.IsNullOrWhiteSpace(p.RejectedBy))
        {
            return ProcessingOutcome.ToDeadLetter("Event does not contain ApplicationRef, ApplicationNumber, BranchCode and RejectedBy.");
        }

        var documentIds = (p.EvidenceDocuments ?? [])
            .Select(x => x.DocumentId)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        logger.LogInformation(
            "Received {EventType} MessageId={MessageId} ApplicationRef={ApplicationRef} ({ApplicationNumber}) RejectedBy={RejectedBy}: {Count} evidence document(s).",
            envelope.EventType, envelope.MessageId, p.ApplicationRef, p.ApplicationNumber, p.RejectedBy, documentIds.Count);

        // Still sent with no documents (an application from before evidence was recorded):
        // DM records the message in its Inbox, so the outcome is visible and never re-applied.
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(envelope, p, documentIds, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                tokenClient.Invalidate();
                response = await SendAsync(envelope, p, documentIds, cancellationToken);
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new TransientProcessingException($"Documents Management API unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Compensated the evidence of application {ApplicationNumber}: {Response}", p.ApplicationNumber, body);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403: this worker's identity is not accepted - a configuration problem, not a bad message.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
                throw new TransientProcessingException($"Documents Management API returned HTTP {status} after the resilience pipeline: {body}");

            return ProcessingOutcome.ToDeadLetter($"Documents Management API rejected the message with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Envelope envelope, Payload p, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        var token = await tokenClient.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/documents/invalidations")
        {
            Content = JsonContent.Create(new
            {
                messageId = envelope.MessageId,
                applicationRef = p.ApplicationRef,
                applicationNumber = p.ApplicationNumber,
                branchCode = p.BranchCode,
                rejectedBy = p.RejectedBy,
                documentIds
            })
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