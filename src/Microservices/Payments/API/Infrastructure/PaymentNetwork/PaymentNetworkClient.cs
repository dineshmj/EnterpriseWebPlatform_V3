using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Polly.CircuitBreaker;
using Polly.Timeout;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;

public sealed class PaymentNetworkOptions
{
    public const string SectionName = "PaymentNetwork";

    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>The payment network's API key; configuration / secret store only.</summary>
    public string ApiKey { get; init; } = string.Empty;
}

/// <summary>
/// HTTP adapter to the external payment network (an NPP-style clearing service), and the
/// anti-corruption layer for its wire format. Every request carries an Idempotency-Key
/// (the PaymentRef): the network returns the SAME result for a repeated key, which is what
/// makes retrying this POST safe - a payment can never be sent twice.
///  - 200 / 201 → accepted, with the network's reference;
///  - 422       → refused: a permanent answer (<see cref="PaymentNetworkRefusedException"/>);
///  - anything else, timeouts, open circuit → <see cref="PaymentNetworkUnavailableException"/>.
/// </summary>
public sealed class PaymentNetworkClient(HttpClient http, IOptions<PaymentNetworkOptions> options) : IPaymentNetwork
{
    public const string HttpClientName = "PaymentNetwork";

    public async Task<string> SendAsync(NetworkPaymentRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
            {
                Content = JsonContent.Create(new
                {
                    endToEndId = request.PaymentNumber,
                    debtor = new { bsb = request.FromBsb, accountNumber = request.FromAccountNumber },
                    creditor = new { bsb = request.ToBsb, accountNumber = request.ToAccountNumber, name = request.PayeeName },
                    amount = request.Amount,
                    currency = request.Currency,
                    remittanceInformation = request.Reference
                })
            };
            message.Headers.Add("X-Api-Key", options.Value.ApiKey);
            message.Headers.Add("Idempotency-Key", request.IdempotencyKey.ToString());
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            throw new PaymentNetworkUnavailableException("Payment network unavailable: the circuit breaker is open.", ex);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or TimeoutException or TimeoutRejectedException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new PaymentNetworkUnavailableException($"Payment network unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var refusal = await TryReadAsync<Refusal>(response, cancellationToken);
                throw new PaymentNetworkRefusedException(refusal?.Reason ?? "The payment network refused the payment.");
            }

            if (!response.IsSuccessStatusCode)
                throw new PaymentNetworkUnavailableException($"Payment network returned HTTP {(int)response.StatusCode}.");

            var body = await TryReadAsync<Accepted>(response, cancellationToken);
            if (string.IsNullOrWhiteSpace(body?.NetworkReference))
                throw new PaymentNetworkUnavailableException("Payment network response did not identify the payment.");

            return body.NetworkReference;
        }
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        try { return await response.Content.ReadFromJsonAsync<T>(cancellationToken); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { return null; }
    }

    private sealed record Accepted([property: JsonPropertyName("networkReference")] string? NetworkReference);

    private sealed record Refusal([property: JsonPropertyName("reason")] string? Reason);
}