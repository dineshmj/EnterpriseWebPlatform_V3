using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Polly;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Accounts.AccountApplicationOpeningSubscriber.Processing;

/// <summary>
/// Turns one compliance.case.approved event into "open the account application for this
/// onboarding application" on the Accounts API, and classifies the result:
///  - Processed   : opened (201) or already open (200);
///  - Dead letter : malformed, wrong event type, required fields missing, or 4xx;
///  - Transient   : 401/403/408/429/5xx, timeouts, open circuit - retried in place.
/// The worker holds no account rules: the AccountApplication aggregate does.
/// </summary>
public sealed class AccountApplicationOpeningProcessor(
    IHttpClientFactory httpClientFactory,
    CachedM2MTokenClient tokenClient,
    ILogger<AccountApplicationOpeningProcessor> logger)
    : IMessageProcessor
{
    public const string HttpClientName = "AccountsApi";

    private const string ExpectedEventType = "ComplianceCaseApproved";

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
        if (p is null || p.ApplicationRef == Guid.Empty || p.ComplianceCaseId <= 0 ||
            string.IsNullOrWhiteSpace(p.ApplicationNumber) || string.IsNullOrWhiteSpace(p.CustomerNumber) ||
            string.IsNullOrWhiteSpace(p.BranchCode))
        {
            return ProcessingOutcome.ToDeadLetter(
                "Event does not contain ApplicationRef, ApplicationNumber, CustomerNumber, ComplianceCaseId and BranchCode.");
        }

        logger.LogInformation(
            "Received {EventType} MessageId={MessageId} ComplianceCaseId={ComplianceCaseId} ApplicationRef={ApplicationRef} ({ApplicationNumber}) Branch={Branch}.",
            envelope.EventType, envelope.MessageId, p.ComplianceCaseId, p.ApplicationRef, p.ApplicationNumber, p.BranchCode);

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
                logger.LogInformation("{Outcome} the account application for application {ApplicationNumber}: {Response}",
                    status == 201 ? "Opened" : "Found existing", p.ApplicationNumber, body);
                return ProcessingOutcome.Processed;
            }

            // 401 / 403: this worker's identity is not accepted - a configuration problem, not a bad message.
            if (status is 401 or 403 or 408 or 429 || status >= 500)
                throw new TransientProcessingException($"Accounts API returned HTTP {status} after the resilience pipeline: {body}");

            return ProcessingOutcome.ToDeadLetter($"Accounts API rejected the message with HTTP {status}: {body}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Envelope envelope, Payload p, CancellationToken cancellationToken)
    {
        var token = await tokenClient.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/accounts/applications/from-compliance-approved")
        {
            Content = JsonContent.Create(new
            {
                applicationRef = p.ApplicationRef,
                applicationNumber = p.ApplicationNumber,
                customerNumber = p.CustomerNumber,
                complianceCaseId = p.ComplianceCaseId,
                branchCode = p.BranchCode,
                initiatedByUserId = envelope.InitiatedByUserId,
                complianceApprovedByUserId = p.DecisionByUserId,
                workflowId = envelope.WorkflowId,
                correlationId = envelope.CorrelationId,
                causationId = envelope.MessageId,
                applicant = p.Applicant,
                initiatedByLanId = envelope.InitiatedByLanId,
                complianceApprovedByLanId = p.DecisionByLanId
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
        Payload? Payload,
        string? InitiatedByLanId);

    private sealed record Payload(
        long ComplianceCaseId,
        Guid ApplicationRef,
        string? ApplicationNumber,
        string? CustomerNumber,
        string? BranchCode,
        string? DecisionByUserId,
        ApplicantName? Applicant,
        string? DecisionByLanId);

    // The applicant's name as Compliance cleared it; forwarded as-is (Accounts validates it).
    private sealed record ApplicantName(string? FirstName, string? LastName);
}