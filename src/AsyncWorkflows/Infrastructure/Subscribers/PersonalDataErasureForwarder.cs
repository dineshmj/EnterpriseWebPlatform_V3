using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

/// <summary>
/// Where one context takes an erasure: its API's named HttpClient (DPoP-bound machine token
/// already on it), the internal path, and the name the context confirms under ("customer-kyc").
/// </summary>
public sealed record PersonalDataErasureTarget(string HttpClientName, string ErasurePath, string Context);

// ┌─ :: ARCHITECTURE + RESILIENCE :: Each context erases its own copy, then says so ───────────────
// │ Why : Customer Onboarding decides an erasure; KYC, Compliance, Accounts and Documents each hold
// │       a copy only they can reach. The bank must be able to prove every copy went - and a
// │       confirmation must never be sent for a copy that is still there.
// │ How : The context's existing subscriber also reads customer.personal-data.erasure. On
// │       CustomerPersonalDataErased it calls its own API ("erase this customer"; idempotent), and
// │       only after a 2xx publishes PersonalDataCopyErased to the confirmation topic - THEN the
// │       shared loop commits. A crash or failed publish in between leaves the message uncommitted:
// │       it is redelivered, the API answers "done" again, the confirmation goes out. So no Outbox is
// │       needed, in any context. The confirmation's MessageId is derived from the erasure message
// │       and the context, so a repeated confirmation is one audit entry.
// │       Requested and rejected erasures are skipped: nothing to erase yet.
// │ Ref : doc/Crypto-Shredding.md | Idempotent Consumer | Features 1.3.3
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The erasure part of a context's subscriber, shared by all four (one copy, here).</summary>
public sealed class PersonalDataErasureForwarder(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    IKafkaProducer producer,
    TimeProvider clock,
    ILogger<PersonalDataErasureForwarder> logger)
{
    public const string ErasedEventType = "CustomerPersonalDataErased";
    public const string ConfirmedEventType = "PersonalDataCopyErased";

    private const int SchemaVersion = 1;
    private const string ConfirmedStatus = "ERASED";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>True when the message comes from the erasure topic (the processor then hands it here).</summary>
    public static bool Handles(ConsumedMessage message) =>
        string.Equals(message.Topic, KafkaTopicNames.CustomerPersonalDataErasure, StringComparison.Ordinal);

    public async Task<ProcessingOutcome> ForwardAsync(
        ConsumedMessage consumed,
        PersonalDataErasureTarget target,
        CancellationToken cancellationToken)
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

        // Requested or rejected: nothing to erase (yet). Processed, so the partition moves on.
        if (!string.Equals(envelope.EventType, ErasedEventType, StringComparison.Ordinal))
            return ProcessingOutcome.Processed;

        var customerNumber = envelope.Payload?.CustomerNumber;
        if (string.IsNullOrWhiteSpace(customerNumber))
            return ProcessingOutcome.ToDeadLetter($"{ErasedEventType} does not name the customer number.");

        logger.LogInformation(
            "Received {EventType} MessageId={MessageId} CustomerNumber={CustomerNumber} ErasureRequestId={ErasureRequestId}: erasing the {Context} copy.",
            envelope.EventType,
            envelope.MessageId,
            customerNumber,
            envelope.Payload!.ErasureRequestId,
            target.Context);

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(target, envelope, customerNumber, cancellationToken);

            // SEC: a 401 may mean a revoked token or a rotated key: fetch a new token ONCE, never loop.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                tokenClient.Invalidate();
                response = await SendAsync(target, envelope, customerNumber, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new TransientProcessingException($"The {target.Context} API is unavailable: {ex.Message}", ex);
        }

        ErasureAnswer answer;
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            // RESIL: refused identity, time-out, throttling or a server error is not the message's
            //        fault - retry in place. Any other 4xx means the message itself is bad.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
                throw new TransientProcessingException($"The {target.Context} API returned HTTP {status}: {body}");

            if (!response.IsSuccessStatusCode)
                return ProcessingOutcome.ToDeadLetter($"The {target.Context} API rejected the erasure with HTTP {status}: {body}");

            answer = JsonSerializer.Deserialize<ErasureAnswer>(body, JsonOptions) ?? new ErasureAnswer(0, 0);
        }

        await ConfirmAsync(consumed, envelope, customerNumber, target, answer, cancellationToken);
        return ProcessingOutcome.Processed;
    }

    /// <summary>The confirmation's MessageId: the same for the same erasure and context, every time.</summary>
    public static Guid ConfirmationIdOf(Guid erasureMessageId, string context)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{erasureMessageId:N}|{context}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private Task<HttpResponseMessage> SendAsync(
        PersonalDataErasureTarget target,
        Envelope envelope,
        string customerNumber,
        CancellationToken cancellationToken)
    {
        // CausationId = the erasure message; the DPoP-bound token and its proof are added by the client.
        var request = new HttpRequestMessage(HttpMethod.Post, target.ErasurePath)
        {
            Content = JsonContent.Create(new
            {
                customerNumber,
                causationId = envelope.MessageId,
                workflowId = envelope.WorkflowId,
                correlationId = envelope.CorrelationId
            })
        };

        return httpClientFactory.CreateClient(target.HttpClientName).SendAsync(request, cancellationToken);
    }

    private async Task ConfirmAsync(
        ConsumedMessage consumed,
        Envelope erasure,
        string customerNumber,
        PersonalDataErasureTarget target,
        ErasureAnswer answer,
        CancellationToken cancellationToken)
    {
        var confirmationId = ConfirmationIdOf(erasure.MessageId, target.Context);

        // ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
        // │ The standard envelope: workflow metadata at the top level, the fact under "Payload".
        // │ The initiator stays the person who asked for the erasure; no personal data, counts
        // │ only. Doc: doc/Integration-Event-Catalogue.md (PersonalDataCopyErased).
        // └──────────────────────────────────────────────────────────────────────────────────────
        var confirmation = JsonSerializer.Serialize(new
        {
            MessageId = confirmationId,
            EventType = ConfirmedEventType,
            SchemaVersion,
            Source = target.Context,
            OccurredAt = clock.GetUtcNow(),
            erasure.WorkflowId,
            erasure.CorrelationId,
            CausationId = erasure.MessageId,
            erasure.InitiatedByUserId,
            erasure.InitiatedByLanId,
            Payload = new
            {
                erasure.Payload!.ErasureRequestId,
                CustomerNumber = customerNumber,
                Context = target.Context,
                Status = ConfirmedStatus,
                answer.RecordsErased,
                answer.RecordsHeld
            }
        });

        // RESIL: if this publish fails, the exception propagates as transient - the message is
        //        not committed, so the erasure is asked again and the confirmation retried.
        await producer.ProduceAsync(
            KafkaTopicNames.CustomerPersonalDataErasureConfirmations,
            consumed.Key ?? customerNumber,
            confirmation,
            cancellationToken);

        logger.LogInformation(
            "Confirmed the {Context} copy of customer {CustomerNumber} erased ({Erased} now, {Held} held) as {ConfirmationId}.",
            target.Context,
            customerNumber,
            answer.RecordsErased,
            answer.RecordsHeld,
            confirmationId);
    }

    // ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
    // │ Tolerant reader of Customer Onboarding's erasure events: only what is needed here.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private sealed record Envelope(
        Guid MessageId,
        string? EventType,
        Guid? WorkflowId,
        Guid? CorrelationId,
        string? InitiatedByUserId,
        string? InitiatedByLanId,
        ErasurePayload? Payload);

    private sealed record ErasurePayload(long ErasureRequestId, string? CustomerNumber);

    private sealed record ErasureAnswer(int RecordsErased, int RecordsHeld);
}