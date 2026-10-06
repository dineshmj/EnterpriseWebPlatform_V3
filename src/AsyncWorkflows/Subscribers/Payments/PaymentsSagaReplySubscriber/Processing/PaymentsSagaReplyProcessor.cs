using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Payments.PaymentsSagaReplySubscriber.Processing;

/// <summary>
/// Courier: hands one reply from accounts.funds.replies to the payment saga orchestrator in
/// the Payments API, and classifies the result:
///  - Processed   : handled, ignored as late / duplicate, or already processed (200);
///  - Dead letter : malformed, unknown reply type, required fields missing, or 4xx;
///  - Transient   : 401/403/408/429/5xx, timeouts, open circuit - retried in place.
/// The worker decides nothing: the saga decides what each reply means.
/// </summary>
public sealed class PaymentsSagaReplyProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<PaymentsSagaReplyProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "PaymentsApi";

    private static readonly HashSet<string> Replies = new(StringComparer.Ordinal)
    {
        "FundsReserved", "FundsReservationFailed", "FundsSettled", "FundsReleased"
    };

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
        if (envelope.EventType is null || !Replies.Contains(envelope.EventType))
            return ProcessingOutcome.ToDeadLetter($"Unknown reply '{envelope.EventType}'.");
        if (envelope.Payload is null || envelope.Payload.PaymentRef == Guid.Empty)
            return ProcessingOutcome.ToDeadLetter("Reply does not contain PaymentRef.");

        var p = envelope.Payload;
        logger.LogInformation(
            "Received {Reply} MessageId={MessageId} CausationId={CausationId} Payment={PaymentNumber} ({PaymentRef}) Hold={HoldStatus}{Reason}.",
            envelope.EventType, envelope.MessageId, envelope.CausationId, p.PaymentNumber, p.PaymentRef, p.HoldStatus,
            p.Reason is null ? string.Empty : $" Reason={p.Reason}");

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(envelope, p, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                tokenClient.Invalidate();
                response = await SendAsync(envelope, p, cancellationToken);
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new TransientProcessingException($"Payments API unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("{Reply} for payment {PaymentNumber} handed to the saga: {Response}", envelope.EventType, p.PaymentNumber, body);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403: this worker's identity is not accepted - a configuration problem, not a bad message.
            // 409: two deliveries raced on the saga row - try again.
            if (status is 401 or 403 or 408 or 409 or 429 || status >= 500)
                throw new TransientProcessingException($"Payments API returned HTTP {status} after the resilience pipeline: {body}");

            return ProcessingOutcome.ToDeadLetter($"Payments API rejected the reply with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Envelope envelope, Payload p, CancellationToken cancellationToken)
    {
        var token = await tokenClient.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/payment-sagas/replies")
        {
            Content = JsonContent.Create(new
            {
                messageId = envelope.MessageId,
                replyType = envelope.EventType,
                paymentRef = p.PaymentRef,
                reasonCode = p.Reason,
                reason = p.ReasonText,
                nothingWasHeld = p.NothingWasHeld
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
    }

    // Tolerant reader: only the fields the saga needs; unknown fields are ignored.
    private sealed record Envelope(
        Guid MessageId,
        string? EventType,
        Guid? CausationId,
        Payload? Payload);

    private sealed record Payload(
        Guid PaymentRef,
        string? PaymentNumber,
        string? HoldStatus,
        string? Reason,
        string? ReasonText,
        bool? NothingWasHeld);
}