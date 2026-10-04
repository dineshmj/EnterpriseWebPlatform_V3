using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Models;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Processing;

/// <summary>
/// Turns one onboarding.application.submitted event into a call to the Customer
/// KYC API ("open the KYC case for this application") and classifies the result:
///  - Processed   : the API opened the case (201) or it already existed (200).
///  - Dead letter : the message can never succeed (malformed, wrong event type,
///                  required fields missing, request rejected as invalid).
///  - Transient   : thrown as TransientProcessingException (timeouts, 5xx, open
///                  circuit, IDP unavailable, 401/403) - retried in place by the
///                  shared consume loop (KafkaSubscriberHostedService).
/// The HTTP call runs through a resilience pipeline (timeout, retry with jittered
/// back-off, circuit breaker); the endpoint is idempotent (Inbox + one case per
/// application), so retrying the POST is safe.
/// The worker holds no KYC business rules: the KycCase aggregate does.
/// </summary>
public sealed class KycCaseOpeningProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<KycCaseOpeningProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "CustomerKycApi";

    private const string ExpectedEventType = "OnboardingApplicationSubmitted";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ProcessingOutcome> ProcessAsync(ConsumedMessage consumed, CancellationToken cancellationToken)
    {
        ApplicationSubmittedMessage message;
        try
        {
            var parsed = Parse(consumed.Value);
            if (parsed.Message is null)
                return ProcessingOutcome.ToDeadLetter(parsed.Error!);
            message = parsed.Message;
        }
        catch (JsonException ex)
        {
            return ProcessingOutcome.ToDeadLetter($"Malformed JSON: {ex.Message}");
        }

        // DEBUG POINT #1: a valid submission, about to become a KYC case.
        logger.LogInformation(
            "Received {EventType} MessageId={MessageId} ApplicationRef={ApplicationRef} ({ApplicationNumber}) CustomerNumber={CustomerNumber} Branch={BranchCode} WorkflowId={WorkflowId}.",
            message.EventType,
            message.MessageId,
            message.ApplicationRef,
            message.ApplicationNumber,
            message.CustomerNumber,
            message.BranchCode,
            message.WorkflowId);

        if (string.IsNullOrWhiteSpace(message.InitiatedByUserId))
        {
            logger.LogWarning(
                "Message {MessageId} has no InitiatedByUserId; the KYC case cannot attribute its initiator, and separation of duties will deny every decision on it.",
                message.MessageId);
        }

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
            throw new TransientProcessingException($"Customer KYC API unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "{Outcome} the KYC case for application {ApplicationNumber} (MessageId={MessageId}): {Response}",
                    status == 201 ? "Opened" : "Found existing",
                    message.ApplicationNumber,
                    message.MessageId,
                    body);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403 mean this worker's identity is not (yet) accepted - a configuration
            // problem, not a bad message: retry until it is fixed rather than dead-letter everything.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
            {
                throw new TransientProcessingException(
                    $"Customer KYC API returned HTTP {status} after the resilience pipeline: {body}");
            }

            // 400 / 404 / 409 / 422 ...: this message can never succeed.
            return ProcessingOutcome.ToDeadLetter($"Customer KYC API rejected the message with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(ApplicationSubmittedMessage message, CancellationToken cancellationToken)
    {
        var accessToken = await tokenClient.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/kyc/cases/from-application-submitted")
        {
            // CausationId = this message: the KYC API records it in its Inbox and as
            // the cause of the KycCaseCreated event.
            Content = JsonContent.Create(new
            {
                applicationRef = message.ApplicationRef,
                applicationNumber = message.ApplicationNumber,
                customerNumber = message.CustomerNumber,
                branchCode = message.BranchCode,
                initiatedByUserId = message.InitiatedByUserId,
                workflowId = message.WorkflowId,
                correlationId = message.CorrelationId,
                causationId = message.MessageId
            })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // DEBUG POINT #2: the authenticated, resilient call to the Customer KYC API.
        return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Reads the standard envelope (workflow metadata at the top level, the event
    /// under "Payload") as a tolerant reader: unknown fields are ignored.
    /// </summary>
    private static (ApplicationSubmittedMessage? Message, string? Error) Parse(string payload)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(payload, JsonOptions);
        if (envelope is null || envelope.MessageId == Guid.Empty)
            return (null, "Message has no MessageId.");

        if (!string.Equals(envelope.EventType, ExpectedEventType, StringComparison.Ordinal))
            return (null, $"Unexpected event type '{envelope.EventType}'; expected {ExpectedEventType}.");

        var application = envelope.Payload;
        if (application is null ||
            application.ApplicationRef == Guid.Empty ||
            string.IsNullOrWhiteSpace(application.ApplicationNumber) ||
            string.IsNullOrWhiteSpace(application.CustomerNumber) ||
            string.IsNullOrWhiteSpace(application.BranchCode))
        {
            return (null, "Event does not contain ApplicationRef, ApplicationNumber, CustomerNumber and BranchCode.");
        }

        return (new ApplicationSubmittedMessage(
            envelope.MessageId,
            ExpectedEventType,
            envelope.OccurredAt,
            application.ApplicationRef,
            application.ApplicationNumber,
            application.CustomerNumber,
            application.BranchCode,
            envelope.InitiatedByUserId,
            envelope.WorkflowId,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.Source), null);
    }

    private sealed record Envelope(
        Guid MessageId,
        string? EventType,
        string? Source,
        DateTimeOffset OccurredAt,
        Guid? WorkflowId,
        Guid? CorrelationId,
        Guid? CausationId,
        string? InitiatedByUserId,
        EnvelopePayload? Payload);

    private sealed record EnvelopePayload(
        Guid ApplicationRef,
        string? ApplicationNumber,
        string? CustomerNumber,
        string? BranchCode);
}