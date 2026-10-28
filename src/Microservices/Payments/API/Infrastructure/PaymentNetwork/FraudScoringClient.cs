using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Polly.CircuitBreaker;
using Polly.Timeout;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;

// ┌─ :: ARCHITECTURE + SECURITY :: Anti-corruption layer to the network's fraud scoring ───────────
// │ Why : The provider's wire format and failure modes must not reach the domain, and an answer
// │       this API cannot fully read must never count as a decision - least of all as "allow".
// │ How : The same network, API key and address as the payments (PaymentNetworkOptions), its own
// │       pipeline (budget timeout + circuit breaker, no retry). Only a 200 with a known decision
// │       and a score 0-999 is an answer; anything else - late, 4xx / 5xx, a redirect, a body that
// │       is not JSON, an unknown decision - is "unavailable", and the fallback rules decide.
// │       Nothing about the staff member is sent; the customer number is the bank's own pseudonym.
// │ Ref : Payments API README (fraud check) | DDD Anti-Corruption Layer | CWE-20
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class FraudScoringClient(HttpClient http, IOptions<PaymentNetworkOptions> options) : IFraudScoring
{
    public const string HttpClientName = "FraudScoring";

    public async Task<FraudScoringResult> ScoreAsync(FraudScoringRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/fraud-scores")
            {
                Content = JsonContent.Create(new
                {
                    endToEndId = request.PaymentNumber,
                    customerNumber = request.CustomerNumber,
                    debtor = new { bsb = request.FromBsb, accountNumber = request.FromAccountNumber },
                    creditor = new { bsb = request.ToBsb, accountNumber = request.ToAccountNumber, name = request.PayeeName },
                    amount = request.Amount,
                    currency = request.Currency,
                    channel = "ASSISTED",
                    firstPaymentToPayee = request.FirstPaymentToPayee
                })
            };
            message.Headers.Add("X-Api-Key", options.Value.ApiKey);
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            throw new FraudScoringUnavailableException("the circuit breaker is open", ex);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or TimeoutException or TimeoutRejectedException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new FraudScoringUnavailableException(ex.Message, ex);
        }

        using (response)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new FraudScoringUnavailableException($"HTTP {(int)response.StatusCode}");

            var body = await TryReadAsync(response, cancellationToken);
            if (body?.Score is not { } score || score is < 0 or > FraudAssessment.MaxScore)
                throw new FraudScoringUnavailableException("an answer without a score from 0 to 999");

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Only the three known decisions count; an unknown or missing one is never read as ALLOW.
            // └──────────────────────────────────────────────────────────────────────────────────────
            FraudDecision decision;
            try { decision = PaymentsCodes.ParseFraudDecision(body.Decision?.Trim().ToUpperInvariant() ?? string.Empty); }
            catch (ArgumentOutOfRangeException) { throw new FraudScoringUnavailableException($"an unknown decision \"{body.Decision}\""); }

            return new FraudScoringResult(score, decision, body.Reasons?.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray() ?? []);
        }
    }

    private static async Task<Answer?> TryReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadFromJsonAsync<Answer>(cancellationToken); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { return null; }
    }

    private sealed record Answer(
        [property: JsonPropertyName("score")] int? Score,
        [property: JsonPropertyName("decision")] string? Decision,
        [property: JsonPropertyName("reasons")] string[]? Reasons);
}