using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Accounts.AccountsCommandSubscriber.Processing;

/// <summary>
/// Courier: hands one funds command from accounts.commands (sent by the Payments saga
/// orchestrator) to the Accounts API, and classifies the result:
///  - Processed   : applied or repeated (200);
///  - Dead letter : malformed, unknown command, required fields missing, or 4xx (400 / 404 / 409);
///  - Transient   : 401/403/408/429/5xx, timeouts, open circuit - retried in place.
/// The worker decides nothing: Accounts applies the command to its own data and replies
/// through its own Outbox (accounts.funds.replies).
/// </summary>
public sealed class AccountsCommandProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<AccountsCommandProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "AccountsApi";

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal) { "ReserveFunds", "SettleFunds", "ReleaseFunds" };

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
        if (envelope.EventType is null || !Commands.Contains(envelope.EventType))
            return ProcessingOutcome.ToDeadLetter($"Unknown command '{envelope.EventType}'; expected ReserveFunds, SettleFunds or ReleaseFunds.");

        var p = envelope.Payload;
        if (p is null || p.PaymentRef == Guid.Empty || string.IsNullOrWhiteSpace(p.PaymentNumber) ||
            string.IsNullOrWhiteSpace(p.CustomerNumber) || string.IsNullOrWhiteSpace(p.Bsb) || string.IsNullOrWhiteSpace(p.AccountNumber))
        {
            return ProcessingOutcome.ToDeadLetter("Command does not contain PaymentRef, PaymentNumber, CustomerNumber, Bsb and AccountNumber.");
        }

        logger.LogInformation(
            "Received {Command} MessageId={MessageId} Payment={PaymentNumber} ({PaymentRef}) Account={Bsb} {AccountNumber} Amount={Amount} {Currency}.",
            envelope.EventType, envelope.MessageId, p.PaymentNumber, p.PaymentRef, p.Bsb, p.AccountNumber, p.Amount, p.Currency);

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
            throw new TransientProcessingException($"Accounts API unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("{Command} for payment {PaymentNumber} handled by Accounts: {Response}", envelope.EventType, p.PaymentNumber, body);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403: this worker's identity is not accepted - a configuration problem, not a bad message.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
                throw new TransientProcessingException($"Accounts API returned HTTP {status} after the resilience pipeline: {body}");

            return ProcessingOutcome.ToDeadLetter($"Accounts API rejected the command with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Envelope envelope, Payload p, CancellationToken cancellationToken)
    {
        var token = await tokenClient.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/accounts/funds/commands")
        {
            Content = JsonContent.Create(new
            {
                commandType = envelope.EventType,
                messageId = envelope.MessageId,
                paymentRef = p.PaymentRef,
                paymentNumber = p.PaymentNumber,
                customerNumber = p.CustomerNumber,
                bsb = p.Bsb,
                accountNumber = p.AccountNumber,
                amount = p.Amount,
                currency = p.Currency,
                initiatedByUserId = envelope.InitiatedByUserId,
                workflowId = envelope.WorkflowId,
                correlationId = envelope.CorrelationId
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
    }

    // Tolerant reader: only the fields Accounts needs; unknown fields are ignored.
    private sealed record Envelope(
        Guid MessageId,
        string? EventType,
        Guid? WorkflowId,
        Guid? CorrelationId,
        string? InitiatedByUserId,
        Payload? Payload);

    private sealed record Payload(
        Guid PaymentRef,
        string? PaymentNumber,
        string? CustomerNumber,
        string? Bsb,
        string? AccountNumber,
        decimal Amount,
        string? Currency);
}