using System.Net;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Services;

/// <summary>
/// Retries only safe GET requests. We deliberately do not blindly retry POST/DELETE
/// operations because the current CO and DM APIs do not expose idempotency keys.
/// Retrying a write could otherwise create duplicate customers/documents.
/// </summary>
public sealed class TransientGetRetryHandler : DelegatingHandler
{
    private readonly ILogger<TransientGetRetryHandler> _logger;

    public TransientGetRetryHandler(ILogger<TransientGetRetryHandler> logger)
        => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromMilliseconds(200);

        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (request.Method != HttpMethod.Get ||
                !IsTransient(response.StatusCode) ||
                attempt >= maxAttempts)
            {
                return response;
            }

            response.Dispose();
            _logger.LogWarning(
                "Transient GET failure ({StatusCode}) calling {Uri}; retrying attempt {Attempt}/{MaxAttempts}.",
                (int)response.StatusCode,
                request.RequestUri,
                attempt,
                maxAttempts);

            await Task.Delay(delay, cancellationToken);
            delay *= 2;
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout or
            HttpStatusCode.InternalServerError;
}