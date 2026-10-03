using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Authentication;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Messages;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Processing;

/// <summary>The decision for one consumed message.</summary>
public sealed record ProcessingOutcome(bool DeadLetter, string? Reason)
{
    public static ProcessingOutcome Processed { get; } = new(false, null);

    public static ProcessingOutcome ToDeadLetter(string reason) => new(true, reason);
}

/// <summary>
/// The dependency (IDP, Customer Onboarding API) is unavailable or failing; the
/// message itself is fine. It must be retried - never skipped or dead-lettered.
/// </summary>
public sealed class TransientProcessingException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Turns one KYC case event into a call to the Customer Onboarding API and
/// classifies the result:
///  - Processed   : the API recorded it (Applied / NoChange / Duplicate).
///  - Dead letter : the message can never succeed (malformed, unknown type,
///                  application unknown or number mismatch, request rejected as invalid).
///  - Transient   : thrown as TransientProcessingException (timeouts, 5xx, open
///                  circuit, IDP unavailable) - retried in place by the consumer.
/// The HTTP call itself runs through a resilience pipeline (timeout, retry with
/// jittered back-off, circuit breaker); the endpoint is idempotent per MessageId,
/// so retrying the POST is safe.
/// </summary>
public sealed class KycOutcomeProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<KycOutcomeProcessor> logger)
{
    private static readonly HashSet<string> SupportedEventTypes = new(StringComparer.Ordinal)
    {
        "KycCaseCreated",
        "KycCaseApproved",
        "KycCaseRejected"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ProcessingOutcome> ProcessAsync(string payload, CancellationToken cancellationToken)
    {
        KycOutcomeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<KycOutcomeMessage>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ProcessingOutcome.ToDeadLetter($"Malformed JSON: {ex.Message}");
        }

        if (message is null || message.MessageId == Guid.Empty)
            return ProcessingOutcome.ToDeadLetter("Message has no MessageId.");

        if (!SupportedEventTypes.Contains(message.EventType ?? string.Empty))
            return ProcessingOutcome.ToDeadLetter($"Unsupported event type '{message.EventType}'.");

        if (message.ApplicationId <= 0 || string.IsNullOrWhiteSpace(message.ApplicationNumber))
        {
            // e.g. events produced before KYC cases were linked to applications.
            return ProcessingOutcome.ToDeadLetter("Event does not identify an onboarding application (ApplicationId / ApplicationNumber missing).");
        }

        // DEBUG POINT #1: a valid KYC outcome, about to be recorded on the application.
        logger.LogInformation(
            "Received {EventType} MessageId={MessageId} KycCaseId={KycCaseId} ApplicationId={ApplicationId} ({ApplicationNumber}) WorkflowId={WorkflowId}.",
            message.EventType,
            message.MessageId,
            message.KycCaseId,
            message.ApplicationId,
            message.ApplicationNumber,
            message.WorkflowId);

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(message, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The cached token may have been revoked or the IDP restarted: refresh once.
                response.Dispose();
                tokenClient.Invalidate();
                response = await SendAsync(message, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            // Network failure, timeout or open circuit breaker.
            throw new TransientProcessingException($"Customer Onboarding API unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Recorded {EventType} MessageId={MessageId} on application {ApplicationId}: {Response}",
                    message.EventType,
                    message.MessageId,
                    message.ApplicationId,
                    body);
                return ProcessingOutcome.Processed;
            }

            if (status is 408 or 429 || status >= 500 || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TransientProcessingException(
                    $"Customer Onboarding API returned HTTP {status} after the resilience pipeline: {body}");
            }

            // 400 / 403 / 404 / 409 / 422 ...: this message can never succeed.
            return ProcessingOutcome.ToDeadLetter($"Customer Onboarding API rejected the message with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(KycOutcomeMessage message, CancellationToken cancellationToken)
    {
        var accessToken = await tokenClient.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/internal/v1/onboarding/applications/{message.ApplicationId}/kyc-outcomes")
        {
            // ApplicationNumber lets the API reject a fact whose ApplicationId now
            // belongs to a different application (ids are reused when a DB is recreated).
            Content = JsonContent.Create(new
            {
                messageId = message.MessageId,
                eventType = message.EventType,
                applicationNumber = message.ApplicationNumber
            })
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        // Workflow metadata: the resulting Customer Onboarding events stay in the same
        // workflow, are caused by THIS KYC message, and keep the human initiator.
        if (message.WorkflowId is { } workflowId)
            request.Headers.Add("X-Workflow-Id", workflowId.ToString());
        if (message.CorrelationId is { } correlationId)
            request.Headers.Add("X-Correlation-Id", correlationId.ToString());
        request.Headers.Add("X-Causation-Id", message.MessageId.ToString());
        if (!string.IsNullOrWhiteSpace(message.InitiatedByUserId))
            request.Headers.Add("X-Initiated-By-User-Id", message.InitiatedByUserId);

        // DEBUG POINT #2: the authenticated, resilient call to the Customer Onboarding API.
        return await httpClientFactory
            .CreateClient("CustomerOnboardingApi")
            .SendAsync(request, cancellationToken);
    }
}
