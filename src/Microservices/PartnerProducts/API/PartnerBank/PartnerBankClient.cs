using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;

/// <summary>What the Partner Bank answered to an instruction.</summary>
public sealed record PartnerAnswer(PartnerOutcome Outcome, JsonObject? Body, string? Reason);

public enum PartnerOutcome
{
    /// <summary>Done (201), or the same instruction already done (200).</summary>
    Accepted,

    /// <summary>The Partner Bank refused it (400 invalid, 409 conflict, 404 unknown account): a final answer.</summary>
    Refused,

    /// <summary>No answer: unreachable, timed out, 5xx or our own credentials refused. Not final - send the same instruction again.</summary>
    Unavailable
}

// ┌─ :: ARCHITECTURE + SECURITY :: The Partner Bank adapter (EWP as distributor) ──────────────────
// │ Why : Opening an account and buying gold at ANOTHER bank: the Partner Bank must know it is EWP
// │       calling, and must be able to prove later exactly what EWP instructed.
// │ How : Mutual TLS with EWP's client certificate (the server trusted only through the PoC's own
// │       root, no redirects); EWP's OWN token (client credentials, scope partner.distribution, bound
// │       by the Partner Bank to this certificate); each instruction a JWT SIGNED with EWP's
// │       request-signing key (ES256, typ partner-instruction+jwt, 5 minutes, jti) sent as the body.
// │       The jti is EWP's stable instruction ID: sending again after a lost answer is never a
// │       second account or order. Answers translated to Accepted / Refused (final) / Unavailable.
// │ Ref : Partner Bank repo doc/Architecture.md D14, README 18d-18h | RFC 8705 | RFC 7515 |
// │       EWP Threat-Model TB6
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class PartnerBankClient(
    HttpClient http,
    PartnerBankOptions options,
    PartnerBankCredentials credentials,
    TimeProvider clock,
    ILogger<PartnerBankClient> logger)
{
    public const string HttpClientName = "PartnerBank";
    private const string Audience = "partner-api";
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Token, DateTimeOffset ExpiresAt)? cachedToken;

    public Task<PartnerAnswer> OpenAccountAsync(JsonObject instruction, Guid instructionId, CancellationToken cancellationToken) =>
        InstructAsync("/api/distribution/gold-accounts", instruction, instructionId, cancellationToken);

    /// <summary>Pushes a signed staff_account_view (PAR-style): the answer carries a one-time request_uri.</summary>
    public Task<PartnerAnswer> PushStaffViewAsync(JsonObject instruction, Guid instructionId, CancellationToken cancellationToken) =>
        InstructAsync("/api/distribution/staff-views", instruction, instructionId, cancellationToken);

    public Task<PartnerAnswer> PlacePurchaseAsync(JsonObject instruction, Guid instructionId, CancellationToken cancellationToken) =>
        InstructAsync("/api/distribution/gold-orders", instruction, instructionId, cancellationToken);

    /// <summary>The account's holding as the Partner Bank keeps it (grams, today's value, activity) - null when unavailable.</summary>
    public Task<JsonObject?> HoldingAsync(string accountNumber, CancellationToken cancellationToken) =>
        GetAsync($"/api/distribution/gold-accounts/{Uri.EscapeDataString(accountNumber)}", cancellationToken);

    /// <summary>
    /// The Digital Gold account the Partner Bank holds for an EWP customer that EWP itself introduced
    /// (number, holder's name) - null when there is none, or the Partner Bank cannot be reached.
    /// </summary>
    public Task<JsonObject?> AccountOfCustomerAsync(string ewpCustomerNumber, CancellationToken cancellationToken) =>
        GetAsync($"/api/distribution/gold-accounts?ewpCustomerNumber={Uri.EscapeDataString(ewpCustomerNumber)}", cancellationToken);

    private async Task<JsonObject?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(cancellationToken);
        if (token is null)
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.ApiBaseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Partner Bank GET {Path} answered HTTP {Status}.", path, (int)response.StatusCode);
                return null;
            }
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Partner Bank GET {Path} unavailable: {Reason}", path, e.Message);
            return null;
        }
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The payment advice and the cancellation follow an order EWP itself placed: same mutual
    // │ TLS and bound token; the Partner Bank accepts them only for ITS distributor's order,
    // │ the advice only for the exact amount, and the same advice twice allocates once.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public Task<PartnerAnswer> PaymentAdviceAsync(string orderNumber, string networkReference, decimal amount, CancellationToken cancellationToken) =>
        PostAsync($"/api/gold-orders/{Uri.EscapeDataString(orderNumber)}/payment-advice",
            new StringContent(new JsonObject { ["networkReference"] = networkReference, ["amount"] = amount }.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);

    public Task<PartnerAnswer> CancelOrderAsync(string orderNumber, string reason, CancellationToken cancellationToken) =>
        PostAsync($"/api/gold-orders/{Uri.EscapeDataString(orderNumber)}/cancel",
            new StringContent(new JsonObject { ["reason"] = reason.Length > 200 ? reason[..200] : reason }.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);

    private Task<PartnerAnswer> InstructAsync(string path, JsonObject instruction, Guid instructionId, CancellationToken cancellationToken) =>
        PostAsync(path, new StringContent(Sign(instruction, instructionId), Encoding.ASCII, "application/jwt"), cancellationToken);

    private async Task<PartnerAnswer> PostAsync(string path, HttpContent content, CancellationToken cancellationToken)
    {
        using var ownedContent = content;
        var token = await TokenAsync(cancellationToken);
        if (token is null)
            return new PartnerAnswer(PartnerOutcome.Unavailable, null, "EWP could not get its token from the Partner Bank.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.ApiBaseUrl.TrimEnd('/')}{path}") { Content = ownedContent };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var body = TryParse(text);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created or HttpStatusCode.OK:
                    return new PartnerAnswer(PartnerOutcome.Accepted, body, null);
                case HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound:
                    return new PartnerAnswer(PartnerOutcome.Refused, body, ReasonOf(body) ?? $"The Partner Bank refused the instruction (HTTP {(int)response.StatusCode}).");
                default:
                    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
                    // │ 401 / 403 / 415 / 5xx: our credentials or the Partner Bank itself - not an answer about the customer.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    logger.LogWarning("Partner Bank {Path} answered HTTP {Status}: {Reason}", path, (int)response.StatusCode, ReasonOf(body));
                    if (response.StatusCode is HttpStatusCode.Unauthorized)
                        cachedToken = null;
                    return new PartnerAnswer(PartnerOutcome.Unavailable, null, $"The Partner Bank did not accept the call (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Partner Bank {Path} unreachable: {Reason}", path, e.Message);
            return new PartnerAnswer(PartnerOutcome.Unavailable, null, "The Partner Bank could not be reached.");
        }
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The instruction is signed with EWP's request-signing key: ES256, the certificate's
    // │ thumbprint as kid, issuer = EWP's client ID (the caller), audience partner-api, a
    // │ 5-minute life, and EWP's stable instruction ID as jti (re-signed on every attempt,
    // │ the jti never changes - the Partner Bank does each instruction once).
    // └──────────────────────────────────────────────────────────────────────────────────────
    private string Sign(JsonObject instruction, Guid instructionId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var key = new ECDsaSecurityKey(credentials.SigningCertificate.GetECDsaPrivateKey()!) { KeyId = credentials.SigningCertificate.Thumbprint };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.ClientId,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            TokenType = "partner-instruction+jwt",
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["jti"] = instructionId.ToString(),
                ["instruction"] = JsonDocument.Parse(instruction.ToJsonString()).RootElement.Clone()
            }
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    // ┌─ PERF ───────────────────────────────────────────────────────────────────────────────
    // │ EWP's own token at the Partner Bank: client credentials over mutual TLS, kept until shortly before it expires.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private async Task<string?> TokenAsync(CancellationToken cancellationToken)
    {
        if (cachedToken is { } valid && valid.ExpiresAt > clock.GetUtcNow().AddSeconds(30))
            return valid.Token;

        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedToken is { } again && again.ExpiresAt > clock.GetUtcNow().AddSeconds(30))
                return again.Token;

            using var response = await http.PostAsync(options.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["scope"] = "partner.distribution"
            }), cancellationToken);
            var body = TryParse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!response.IsSuccessStatusCode || (string?)body?["access_token"] is not { } token)
            {
                logger.LogWarning("Partner Bank token endpoint answered HTTP {Status}: {Error}", (int)response.StatusCode, (string?)body?["error"]);
                return null;
            }
            var lifetime = body["expires_in"]?.GetValue<int>() ?? 300;
            cachedToken = (token, clock.GetUtcNow().AddSeconds(lifetime));
            return token;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Partner Bank token endpoint unreachable: {Reason}", e.Message);
            return null;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static JsonObject? TryParse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>The Partner Bank's own words: a problem's detail, or the first validation message.</summary>
    private static string? ReasonOf(JsonObject? body) =>
        (string?)body?["detail"]
        ?? (body?["errors"] as JsonObject)?.Select(e => (e.Value as JsonArray)?.FirstOrDefault()?.GetValue<string>()).FirstOrDefault(m => m is not null);
}