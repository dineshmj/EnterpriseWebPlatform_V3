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
        var message = DeserializeApplicationSubmittedMessage(payload);

        // DEBUG POINT #1: Put a breakpoint here to inspect the Kafka event before any downstream call.
        logger.LogInformation(
            "Received onboarding.application.submitted. MessageId={MessageId}, ApplicationNumber={ApplicationNumber}, CustomerNumber={CustomerNumber}, InitiatedByUserId={InitiatedByUserId}, WorkflowId={WorkflowId}, CorrelationId={CorrelationId}, CausationId={CausationId}, KafkaKey={KafkaKey}",
            message.MessageId,
            message.ApplicationNumber,
            message.CustomerNumber,
            message.InitiatedByUserId,
            message.WorkflowId,
            message.CorrelationId,
            message.CausationId,
            key);

        if (!string.Equals(message.EventType, "OnboardingApplicationSubmitted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unexpected event type '{message.EventType}' on topic '{_options.Topic}'.");
        }

        if (message.ApplicationId <= 0 ||
            string.IsNullOrWhiteSpace(message.ApplicationNumber) ||
            string.IsNullOrWhiteSpace(message.CustomerNumber))
        {
            throw new InvalidOperationException(
                "OnboardingApplicationSubmitted event did not contain ApplicationId, ApplicationNumber and CustomerNumber.");
        }

        if (string.IsNullOrWhiteSpace(message.InitiatedByUserId))
        {
            logger.LogWarning(
                "OnboardingApplicationSubmitted event {MessageId} does not contain InitiatedByUserId. The KYC case will be created without human initiator attribution.",
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

    private static ApplicationSubmittedMessage DeserializeApplicationSubmittedMessage(string payload)
    {
        // onboarding.application.submitted is published in the standard envelope:
        // workflow metadata at the top level, the event itself under "Payload".
        var envelope = JsonSerializer.Deserialize<ApplicationSubmittedEnvelope>(payload, JsonOptions)
            ?? throw new InvalidOperationException("OnboardingApplicationSubmitted envelope could not be deserialized.");

        var application = envelope.Payload
            ?? throw new InvalidOperationException("OnboardingApplicationSubmitted envelope did not contain a Payload.");

        return new ApplicationSubmittedMessage(
            envelope.MessageId,
            envelope.EventType,
            envelope.OccurredAt,
            application.ApplicationId,
            application.ApplicationNumber,
            application.CustomerNumber,
            envelope.InitiatedByUserId,
            envelope.WorkflowId,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.Source);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Tolerant reader: only the fields KYC needs; unknown fields are ignored.
    private sealed record ApplicationSubmittedEnvelope(
        Guid MessageId,
        string EventType,
        string? Source,
        DateTimeOffset OccurredAt,
        Guid? WorkflowId,
        Guid? CorrelationId,
        Guid? CausationId,
        string? InitiatedByUserId,
        ApplicationSubmittedPayload? Payload);

    private sealed record ApplicationSubmittedPayload(
        long ApplicationId,
        long CustomerId,
        string ApplicationNumber,
        string CustomerNumber);

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Customer KYC Subscriber is stopping.");
        return base.StopAsync(cancellationToken);
    }
}