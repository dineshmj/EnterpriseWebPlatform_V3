using System.Text.Json;

using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Authentication;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Clients;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Consumer;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Models;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.HostedServices;

public sealed class CustomerKycSubscriberHostedService(
    IKafkaConsumer consumer,
    M2MTokenClient tokenClient,
    CustomerKycApiClient kycApiClient,
    IOptions<CustomerKycSubscriberOptions> options,
    ILogger<CustomerKycSubscriberHostedService> logger)
    : BackgroundService
{
    private readonly CustomerKycSubscriberOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Customer KYC Subscriber started. Topic={Topic}, GroupId={GroupId}",
            _options.Topic,
            _options.GroupId);

        await consumer.ConsumeAsync(
            _options.Topic,
            _options.GroupId,
            HandleMessageAsync,
            stoppingToken);
    }

    private async Task HandleMessageAsync(
        string key,
        string payload,
        CancellationToken cancellationToken)
    {
        var message = DeserializeCustomerCreatedMessage(payload);

        // DEBUG POINT #1: Put a breakpoint here to inspect the Kafka event before any downstream call.
        logger.LogInformation(
            "Received customer.created. MessageId={MessageId}, CustomerNumber={CustomerNumber}, InitiatedByUserId={InitiatedByUserId}, WorkflowId={WorkflowId}, CorrelationId={CorrelationId}, CausationId={CausationId}, KafkaKey={KafkaKey}",
            message.MessageId,
            message.CustomerNumber,
            message.InitiatedByUserId,
            message.WorkflowId,
            message.CorrelationId,
            message.CausationId,
            key);

        if (!string.Equals(message.EventType, "CustomerCreated", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unexpected event type '{message.EventType}' on topic '{_options.Topic}'.");
        }

        if (string.IsNullOrWhiteSpace(message.CustomerNumber))
            throw new InvalidOperationException("CustomerCreated event did not contain CustomerNumber.");

        if (string.IsNullOrWhiteSpace(message.InitiatedByUserId))
        {
            logger.LogWarning(
                "CustomerCreated event {MessageId} does not contain InitiatedByUserId. The KYC case will be created without human initiator attribution.",
                message.MessageId);
        }

        Exception? lastException = null;

        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxAttempts); attempt++)
        {
            try
            {
                // DEBUG POINT #2 is inside M2MTokenClient, immediately before the token request.
                var accessToken = await tokenClient.GetAccessTokenAsync(cancellationToken);

                // DEBUG POINT #3 is inside CustomerKycApiClient, immediately before the API call.
                await kycApiClient.CallAsync(message, accessToken, cancellationToken);

                logger.LogInformation(
                    "Customer KYC workflow step completed successfully for MessageId={MessageId}, CustomerNumber={CustomerNumber}, Attempt={Attempt}.",
                    message.MessageId,
                    message.CustomerNumber,
                    attempt);

                // KafkaConsumer commits the offset only after this handler completes successfully.
                return;
            }
            catch (TransientKycApiException ex) when (attempt < Math.Max(1, _options.MaxAttempts))
            {
                lastException = ex;
                var delay = TimeSpan.FromMilliseconds(
                    _options.InitialRetryDelayMilliseconds * Math.Pow(2, attempt - 1));

                logger.LogWarning(
                    ex,
                    "Transient KYC API failure for MessageId={MessageId}. Retrying in {Delay}. Attempt={Attempt}/{MaxAttempts}.",
                    message.MessageId,
                    delay,
                    attempt,
                    _options.MaxAttempts);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Customer KYC workflow step failed after {_options.MaxAttempts} attempts for MessageId={message.MessageId}.",
            lastException);
    }

    private static CustomerCreatedMessage DeserializeCustomerCreatedMessage(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        // Current V3 customer.created messages are envelopes containing Payload.
        if (root.TryGetProperty("Payload", out var payloadElement) ||
            root.TryGetProperty("payload", out payloadElement))
        {
            var envelope = JsonSerializer.Deserialize<CustomerCreatedEnvelope>(
                payload,
                JsonOptions);

            if (envelope is null)
                throw new InvalidOperationException("CustomerCreated event envelope could not be deserialized.");

            var customer = JsonSerializer.Deserialize<CustomerCreatedPayload>(
                payloadElement.GetRawText(),
                JsonOptions);

            if (customer is null)
                throw new InvalidOperationException("CustomerCreated event payload could not be deserialized.");

            return new CustomerCreatedMessage(
                envelope.MessageId,
                envelope.EventType,
                envelope.OccurredAt,
                customer.CustomerNumber,
                envelope.InitiatedByUserId ?? customer.InitiatedByUserId,
                envelope.WorkflowId,
                envelope.CorrelationId,
                envelope.CausationId,
                envelope.Source);
        }

        // Compatibility with the older direct-event shape used by the original subscriber.
        var direct = JsonSerializer.Deserialize<LegacyCustomerCreatedEvent>(payload, JsonOptions)
            ?? throw new InvalidOperationException("CustomerCreated event could not be deserialized.");

        return new CustomerCreatedMessage(
            direct.MessageId,
            direct.EventType,
            direct.OccurredAt,
            direct.CustomerNumber,
            direct.InitiatedByUserId,
            null,
            ParseNullableGuid (direct.CorrelationId),
            ParseNullableGuid (direct.CausationId),
            null);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CustomerCreatedEnvelope(
        Guid MessageId,
        string EventType,
        string? Source,
        DateTimeOffset OccurredAt,
        Guid? WorkflowId,
        Guid? CorrelationId,
        Guid? CausationId,
        string? InitiatedByUserId);

    private sealed record CustomerCreatedPayload(
        long CustomerId,
        string CustomerNumber,
        Guid? SubjectId,
        string CustomerType,
        string Status,
        string? InitiatedByUserId = null);

    private sealed record LegacyCustomerCreatedEvent(
        Guid MessageId,
        string EventType,
        DateTimeOffset OccurredAt,
        string CustomerNumber,
        string? FirstName,
        string? LastName,
        string? Email,
        string? CustomerType,
        string? Status,
        long? BranchId,
        string? InitiatedByUserId = null,
        string? CorrelationId = null,
        string? CausationId = null);

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Customer KYC Subscriber is stopping.");
        return base.StopAsync(cancellationToken);
    }

    private static Guid? ParseNullableGuid(string? value)
    {
        return Guid.TryParse(value, out var guid)
            ? guid
            : null;
    }
}