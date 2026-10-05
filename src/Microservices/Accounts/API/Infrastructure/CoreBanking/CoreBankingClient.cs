using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Polly.CircuitBreaker;
using Polly.Timeout;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.CoreBanking;

public sealed class CoreBankingOptions
{
    public const string SectionName = "CoreBanking";

    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>The core-banking system's API key; configuration / secret store only.</summary>
    public string ApiKey { get; init; } = string.Empty;

    public int PollIntervalSeconds { get; init; } = 5;

    public int RetryInitialDelaySeconds { get; init; } = 15;

    public int RetryMaxDelaySeconds { get; init; } = 300;

    /// <summary>Failures after which an approved opening is given up (FAILED → compensation).</summary>
    public int MaxOpeningAttempts { get; init; } = 6;
}

/// <summary>
/// HTTP adapter to the external core-banking system, and the anti-corruption layer for
/// its wire format. Every request carries an Idempotency-Key (the onboarding
/// ApplicationRef): core banking returns the SAME account for a repeated key, which is
/// what makes retrying this POST safe (resilience pipeline: see Program.cs).
///  - 200 / 201 → the account (opened now, or earlier for the same key);
///  - 422       → refused: a permanent answer (<see cref="CoreBankingRefusedException"/>);
///  - anything else, timeouts, open circuit → <see cref="CoreBankingUnavailableException"/>.
/// </summary>
public sealed class CoreBankingClient(HttpClient http, IOptions<CoreBankingOptions> options) : ICoreBankingSystem
{
    public const string HttpClientName = "CoreBanking";

    public async Task<OpenAccountResponse> OpenAccountAsync(OpenAccountRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/accounts")
            {
                Content = JsonContent.Create(new
                {
                    customerNumber = request.CustomerNumber,
                    accountName = request.AccountName,
                    branchCode = request.BranchCode,
                    product = request.Product.ToCode()
                })
            };
            message.Headers.Add("X-Api-Key", options.Value.ApiKey);
            message.Headers.Add("Idempotency-Key", request.IdempotencyKey.ToString());
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            throw new CoreBankingUnavailableException("Core banking unavailable: the circuit breaker is open.", ex);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or TimeoutException or TimeoutRejectedException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new CoreBankingUnavailableException($"Core banking unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var refusal = await TryReadAsync<Refusal>(response, cancellationToken);
                throw new CoreBankingRefusedException(refusal?.Reason ?? "The core-banking system refused to open the account.");
            }

            if (!response.IsSuccessStatusCode)
                throw new CoreBankingUnavailableException($"Core banking returned HTTP {(int)response.StatusCode}.");

            var body = await TryReadAsync<OpenedAccount>(response, cancellationToken)
                ?? throw new CoreBankingUnavailableException("Core banking returned an empty response.");

            if (string.IsNullOrWhiteSpace(body.AccountNumber) || string.IsNullOrWhiteSpace(body.Bsb) || string.IsNullOrWhiteSpace(body.Reference))
                throw new CoreBankingUnavailableException("Core banking response did not identify the account.");

            return new OpenAccountResponse(body.AccountNumber, body.Bsb, body.Reference);
        }
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        try { return await response.Content.ReadFromJsonAsync<T>(cancellationToken); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { return null; }
    }

    private sealed record OpenedAccount(
        [property: JsonPropertyName("accountNumber")] string? AccountNumber,
        [property: JsonPropertyName("bsb")] string? Bsb,
        [property: JsonPropertyName("reference")] string? Reference);

    private sealed record Refusal([property: JsonPropertyName("reason")] string? Reason);
}