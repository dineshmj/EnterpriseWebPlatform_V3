using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Notifications.NotificationsSubscriber.Processing;

/// <summary>
/// Hands each consumed workflow event, unchanged, to the Notifications API, which decides
/// who is told what. Classifies the result:
///  - Processed   : notifications stored, nothing to tell, or a duplicate (200);
///  - Dead letter : malformed, no MessageId / EventType, or 4xx;
///  - Transient   : 401/403/408/429/5xx, timeouts, open circuit - retried in place.
/// The worker holds no notification rules: the Notifications API does.
/// </summary>
public sealed class NotificationsProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<NotificationsProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "NotificationsApi";

    public async Task<ProcessingOutcome> ProcessAsync(ConsumedMessage consumed, CancellationToken cancellationToken)
    {
        JsonElement envelope;
        try
        {
            using var document = JsonDocument.Parse(consumed.Value);
            envelope = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return ProcessingOutcome.ToDeadLetter($"Malformed JSON: {ex.Message}");
        }

        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("MessageId", out var idElement) || !idElement.TryGetGuid(out var messageId) || messageId == Guid.Empty ||
            !envelope.TryGetProperty("EventType", out var typeElement) || typeElement.GetString() is not { Length: > 0 } eventType)
        {
            return ProcessingOutcome.ToDeadLetter("Message has no MessageId or EventType.");
        }

        logger.LogInformation("Received {EventType} MessageId={MessageId} from {Topic}.", eventType, messageId, consumed.Topic);

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(messageId, eventType, envelope, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                tokenClient.Invalidate();
                response = await SendAsync(messageId, eventType, envelope, cancellationToken);
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new TransientProcessingException($"Notifications API unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("{EventType} {MessageId}: {Response}", eventType, messageId, body);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403: this worker's identity is not accepted - a configuration problem, not a bad message.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
                throw new TransientProcessingException($"Notifications API returned HTTP {status} after the resilience pipeline: {body}");

            return ProcessingOutcome.ToDeadLetter($"Notifications API rejected the message with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Guid messageId, string eventType, JsonElement envelope, CancellationToken cancellationToken)
    {
        var token = await tokenClient.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/notifications/events")
        {
            Content = JsonContent.Create(new { messageId, eventType, envelope })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
    }
}