using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.OnboardingOutcomeSubscriber.Messages;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.OnboardingOutcomeSubscriber.Processing;

/// <summary>
/// Turns one KYC, Compliance or Accounts event into a call to the Customer Onboarding API
/// (kyc-outcomes / compliance-outcomes / account-outcomes endpoint, by event type) and
/// classifies the result:
///  - Processed   : the API recorded it (Applied / NoChange / Duplicate).
///  - Dead letter : the message can never succeed (malformed, unknown type,
///                  application unknown or number mismatch, request rejected as invalid).
///  - Transient   : thrown as TransientProcessingException (timeouts, 5xx, open
///                  circuit, IDP unavailable, 401/403) - retried in place by the
///                  shared consume loop (KafkaSubscriberHostedService).
/// The HTTP call itself runs through a resilience pipeline (timeout, retry with
/// jittered back-off, circuit breaker); the endpoint is idempotent per MessageId,
/// so retrying the POST is safe.
/// </summary>
public sealed class OnboardingOutcomeProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<OnboardingOutcomeProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "CustomerOnboardingApi";

    /// <summary>Supported event types and the CO endpoint that records each kind of fact.</summary>
    private static readonly Dictionary<string, string> EndpointByEventType = new(StringComparer.Ordinal)
    {
        ["KycCaseCreated"] = "kyc-outcomes",
        ["KycCaseApproved"] = "kyc-outcomes",
        ["KycCaseRejected"] = "kyc-outcomes",
        ["ComplianceCaseCreated"] = "compliance-outcomes",
        ["ComplianceCaseApproved"] = "compliance-outcomes",
        ["ComplianceCaseRejected"] = "compliance-outcomes",
        ["AccountApplicationCreated"] = "account-outcomes",
        ["AccountOpened"] = "account-outcomes",
        ["AccountApplicationRejected"] = "account-outcomes"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ProcessingOutcome> ProcessAsync(ConsumedMessage consumed, CancellationToken cancellationToken)
    {
        OutcomeMessage? message;
        try
        {
            message = OutcomeMessage.Parse(consumed.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ProcessingOutcome.ToDeadLetter($"Malformed JSON: {ex.Message}");
        }

        if (message is null || message.MessageId == Guid.Empty)
            return ProcessingOutcome.ToDeadLetter("Message has no MessageId.");

        if (!EndpointByEventType.ContainsKey(message.EventType ?? string.Empty))
            return ProcessingOutcome.ToDeadLetter($"Unsupported event type '{message.EventType}'.");

        if (message.ApplicationRef == Guid.Empty || string.IsNullOrWhiteSpace(message.ApplicationNumber))
        {
            // e.g. events produced before applications were referenced by ApplicationRef.
            return ProcessingOutcome.ToDeadLetter("Event does not identify an onboarding application (ApplicationRef / ApplicationNumber missing).");
        }

        // DEBUG POINT #1: a valid KYC outcome, about to be recorded on the application.
        logger.LogInformation(
            "Received {EventType} MessageId={MessageId} CaseId={CaseId} ApplicationRef={ApplicationRef} ({ApplicationNumber}) WorkflowId={WorkflowId}.",
            message.EventType,
            message.MessageId,
            message.KycCaseId,
            message.ApplicationRef,
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
                    "Recorded {EventType} MessageId={MessageId} on application {ApplicationRef}: {Response}",
                    message.EventType,
                    message.MessageId,
                    message.ApplicationRef,
                    body);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403 mean this worker's identity is not (yet) accepted - a configuration
            // problem, not a bad message: retry until it is fixed rather than dead-letter everything.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
            {
                throw new TransientProcessingException(
                    $"Customer Onboarding API returned HTTP {status} after the resilience pipeline: {body}");
            }

            // 400 / 404 / 409 / 422 ...: this message can never succeed.
            return ProcessingOutcome.ToDeadLetter($"Customer Onboarding API rejected the message with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(OutcomeMessage message, CancellationToken cancellationToken)
    {
        var accessToken = await tokenClient.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/internal/v1/onboarding/applications/{message.ApplicationRef}/{EndpointByEventType[message.EventType]}")
        {
            // ApplicationNumber lets the API reject a fact whose reference now
            // belongs to a different application (a consistency check).
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
            .CreateClient(HttpClientName)
            .SendAsync(request, cancellationToken);
    }
}