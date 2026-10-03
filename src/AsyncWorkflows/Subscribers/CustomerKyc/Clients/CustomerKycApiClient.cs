using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Models;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Clients;

public sealed class CustomerKycApiClient(
    IHttpClientFactory httpClientFactory,
    IOptions<CustomerKycSubscriberOptions> options,
    ILogger<CustomerKycApiClient> logger)
{
    private readonly CustomerKycSubscriberOptions _options = options.Value;

    public async Task CallAsync(
        ApplicationSubmittedMessage message,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("CustomerKycApi");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/v1/kyc/cases/from-application-submitted")
        {
            Content = JsonContent.Create(new CreateKycCaseRequest(
                message.ApplicationRef,
                message.ApplicationNumber,
                message.CustomerNumber,
                message.BranchCode,
                message.InitiatedByUserId,
                message.WorkflowId,
                message.CorrelationId,
                message.MessageId))
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Workflow-Message-Id", message.MessageId.ToString());

        // DEBUG POINT #3: Put a breakpoint on the next line to inspect the authenticated KYC API call.
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "Customer KYC API processed CustomerNumber={CustomerNumber}. StatusCode={StatusCode}, Response={Response}",
                message.CustomerNumber,
                (int)response.StatusCode,
                responseBody);
            return;
        }

        if (IsTransient(response.StatusCode))
        {
            throw new TransientKycApiException(
                response.StatusCode,
                responseBody);
        }

        throw new InvalidOperationException(
            $"Customer KYC API rejected CustomerNumber={message.CustomerNumber} with HTTP {(int)response.StatusCode}. Response={responseBody}");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private sealed record CreateKycCaseRequest(
        Guid ApplicationRef,
        string ApplicationNumber,
        string CustomerNumber,
        string BranchCode,
        string? InitiatedByUserId,
        Guid? WorkflowId,
        Guid? CorrelationId,
        Guid CausationId);
}

public sealed class TransientKycApiException(HttpStatusCode statusCode, string responseBody)
    : Exception($"Transient Customer KYC API failure: HTTP {(int)statusCode}. {responseBody}");