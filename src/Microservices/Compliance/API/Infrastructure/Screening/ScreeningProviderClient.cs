using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Polly;
using Polly.CircuitBreaker;

using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Screening;

public sealed class ScreeningProviderOptions
{
    public const string SectionName = "ScreeningProvider";

    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Outside Development the only hosts BaseUrl may name (https, no internal address): a
    /// tampered address cannot send the API key elsewhere. See ExternalEndpoint.
    /// </summary>
    public string[] PermittedHosts { get; init; } = [];

    /// <summary>The provider's API key; configuration / secret store only.</summary>
    public string ApiKey { get; init; } = string.Empty;

    public int PollIntervalSeconds { get; init; } = 5;

    public int RetryInitialDelaySeconds { get; init; } = 15;

    public int RetryMaxDelaySeconds { get; init; } = 300;
}

// ┌─ :: ARCHITECTURE + SECURITY :: Calls to the external screening provider ───────────────────────
// │ Why : An outbound call to a configurable URL can be turned against internal systems (SSRF), and
// │       a hung provider can exhaust the API.
// │ How : The endpoint is checked at start-up (https, permitted hosts, no internal addresses
// │       outside Development); no redirects are followed; timeout, retry and circuit breaker per
// │       call; failures become "unavailable", never a decision.
// │ Ref : Features 1.6.3, 2.4.9 | OWASP API7:2023 SSRF | CWE-918 | Circuit Breaker pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// HTTP adapter to the external AML / sanctions / PEP screening provider, and the
/// anti-corruption layer for its wire format. The HttpClient runs through a
/// resilience pipeline (timeout, jittered retry, circuit breaker - see Program.cs);
/// whatever still fails surfaces as <see cref="ScreeningUnavailableException"/>, so
/// the case stays in SCREENING and is retried later.
/// Sends only what screening needs - the applicant's name and residential address,
/// plus the references to correlate the result: data minimisation towards a third
/// party (no contact details).
/// </summary>
public sealed class ScreeningProviderClient(HttpClient http, IOptions<ScreeningProviderOptions> options) : IScreeningProvider
{
    public const string HttpClientName = "ScreeningProvider";

    public async Task<ScreeningResponse> ScreenAsync(ScreeningRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/screenings")
            {
                Content = JsonContent.Create(new
                {
                    customerNumber = request.CustomerNumber,
                    applicationNumber = request.ApplicationNumber,
                    requestId = request.RequestId,
                    subject = new
                    {
                        firstName = request.FirstName,
                        lastName = request.LastName,
                        addressLine1 = request.AddressLine1,
                        city = request.City,
                        state = request.State,
                        postalCode = request.PostalCode,
                        countryCode = request.CountryCode
                    },
                    lists = new[] { "AML", "SANCTIONS", "PEP" }
                })
            };
            message.Headers.Add("X-Api-Key", options.Value.ApiKey);
            response = await http.SendAsync(message, cancellationToken);
        }
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ If the circuit is open, report the provider as unavailable at once - the case is
        // │ retried later, never decided.
        // └──────────────────────────────────────────────────────────────────────────────────────
        catch (BrokenCircuitException ex)
        {
            throw new ScreeningUnavailableException("Screening provider unavailable: the circuit breaker is open.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Polly.Timeout.TimeoutRejectedException
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new ScreeningUnavailableException($"Screening provider unavailable: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ScreeningUnavailableException($"Screening provider returned HTTP {(int)response.StatusCode}.");

            var body = await response.Content.ReadFromJsonAsync<ProviderResponse>(cancellationToken)
                ?? throw new ScreeningUnavailableException("Screening provider returned an empty response.");

            ScreeningOutcome outcome;
            try { outcome = ComplianceCodes.ParseScreeningOutcome(body.Outcome ?? string.Empty); }
            catch (ArgumentOutOfRangeException) { throw new ScreeningUnavailableException($"Screening provider returned an unknown outcome '{body.Outcome}'."); }

            if (string.IsNullOrWhiteSpace(body.ScreeningId) || string.IsNullOrWhiteSpace(body.Provider))
                throw new ScreeningUnavailableException("Screening provider response did not identify the screening.");

            return new ScreeningResponse(outcome, body.Provider, body.ScreeningId);
        }
    }

    private sealed record ProviderResponse(
        [property: JsonPropertyName("screeningId")] string? ScreeningId,
        [property: JsonPropertyName("provider")] string? Provider,
        [property: JsonPropertyName("outcome")] string? Outcome);
}