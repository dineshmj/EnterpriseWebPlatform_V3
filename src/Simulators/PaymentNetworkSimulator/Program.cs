using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

// =============================================================================
// Payment Network Simulator - stands in for the EXTERNAL payment network (an NPP-style
// clearing service that moves the money to the payee's bank), so the Payments saga can
// show what happens when a third party is slow, failing, down or refusing: retries,
// circuit breaker, and compensation (releasing the reserved funds) after a failure.
//
// POST /v1/payments              send a payment (needs X-Api-Key and Idempotency-Key)
// GET  /v1/bsb/{bsb}             BSB directory: bank, branch, state (needs X-Api-Key)
// POST /v1/payee-confirmations   Confirmation of Payee: does the name match the account? (X-Api-Key)
// GET  /admin/behaviour          current behaviour            } localhost only
// PUT  /admin/behaviour          change behaviour at runtime  }
//
// Behaviour: Healthy | Slow (answers after SlowDelaySeconds) | Failing (HTTP 500)
//            | Down (HTTP 503) | Refusing (HTTP 422: a permanent "no").
// Whatever the behaviour, a payee BSB starting with 999 is refused ("account closed"),
// so one payment can show the compensation path without changing the behaviour; a BSB
// that is not in the directory is refused as well.
// Confirmation of Payee (deterministic, for demos): an account number ending in 0 is a
// NO_MATCH, one ending in 9 a CLOSE_MATCH (the bank holds a slightly different name),
// anything else a MATCH.
// Idempotency: the same Idempotency-Key always returns the SAME result, so a client may
// safely retry after a timeout without paying twice.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
var apiKey = builder.Configuration["Simulator:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Simulator:ApiKey is not configured.");

var state = new SimulatorState(
    Enum.Parse<Behaviour>(builder.Configuration["Simulator:Behaviour"] ?? nameof(Behaviour.Healthy), ignoreCase: true),
    builder.Configuration.GetValue("Simulator:SlowDelaySeconds", 12));

// Payments sent (or refused) so far, by idempotency key (in memory: a simulator, not a clearing house).
var results = new ConcurrentDictionary<string, IResult>(StringComparer.Ordinal);
var sent = new ConcurrentDictionary<string, SentPayment>(StringComparer.Ordinal);

// Behaviour travels as text ("Down"), not as a number.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
var log = app.Logger;

// A real network authenticates its participants; constant-time key comparison.
bool Authorised(HttpContext http) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(http.Request.Headers["X-Api-Key"].ToString()), Encoding.UTF8.GetBytes(apiKey));

app.MapGet("/v1/bsb/{bsb}", (HttpContext http, string bsb) =>
{
    if (!Authorised(http))
        return Results.Unauthorized();

    var entry = BsbDirectory.Find(bsb);
    return entry is null ? Results.NotFound(new { reason = $"BSB {bsb} is not in the directory." }) : Results.Ok(entry);
});

app.MapPost("/v1/payee-confirmations", (HttpContext http, PayeeConfirmationRequest request) =>
{
    if (!Authorised(http))
        return Results.Unauthorized();

    var number = request.AccountNumber?.Trim() ?? string.Empty;
    var name = request.AccountName?.Trim() ?? string.Empty;
    var result = number.EndsWith('0') ? new PayeeConfirmation("NO_MATCH", null)
        : number.EndsWith('9') ? new PayeeConfirmation("CLOSE_MATCH", CloseMatchOf(name))
        : new PayeeConfirmation("MATCH", null);

    log.LogInformation("Confirmation of Payee for {Bsb} {Account} \"{Name}\": {Result}.", request.Bsb, number, name, result.Result);
    return Results.Ok(result);
});

app.MapPost("/v1/payments", async (HttpContext http, PaymentRequest request, CancellationToken ct) =>
{
    if (!Authorised(http))
        return Results.Unauthorized();

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
        return Results.BadRequest(new { reason = "An Idempotency-Key header is required." });

    // A repeated request returns the first answer, whatever the behaviour now: the payment
    // was already sent (or refused); only the answer was lost.
    if (sent.TryGetValue(idempotencyKey, out var existing))
    {
        log.LogInformation("Repeated request (key {Key}): payment {EndToEndId} was already sent ({Reference}).", idempotencyKey, request.EndToEndId, existing.NetworkReference);
        return Results.Ok(existing);
    }
    if (results.TryGetValue(idempotencyKey, out var earlierRefusal))
        return earlierRefusal;

    if (request.Creditor?.Bsb?.Replace("-", string.Empty).StartsWith("999", StringComparison.Ordinal) == true)
        return Refuse(idempotencyKey, request, "The payee's account is closed (the payee's bank returned the payment).");
    if (BsbDirectory.Find(request.Creditor?.Bsb) is null)
        return Refuse(idempotencyKey, request, $"BSB {request.Creditor?.Bsb} is not in the BSB directory.");

    var current = state;
    switch (current.Behaviour)
    {
        case Behaviour.Down:
            log.LogWarning("Payment {EndToEndId}: simulating an OUTAGE (503).", request.EndToEndId);
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
        case Behaviour.Failing:
            log.LogWarning("Payment {EndToEndId}: simulating a FAILURE (500).", request.EndToEndId);
            return Results.StatusCode((int)HttpStatusCode.InternalServerError);
        case Behaviour.Refusing:
            return Refuse(idempotencyKey, request, "The payment network rejected the payment: the payee's bank is not reachable for real-time payments.");
        case Behaviour.Slow:
            log.LogWarning("Payment {EndToEndId}: simulating SLOWNESS ({Delay} s).", request.EndToEndId, current.SlowDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(current.SlowDelaySeconds), ct);
            break;
    }

    var payment = sent.GetOrAdd(idempotencyKey, _ => new SentPayment(
        $"NPP{DateTimeOffset.UtcNow:yyMMdd}{Convert.ToHexString(RandomNumberGenerator.GetBytes(5))}",
        "SETTLED",
        DateTimeOffset.UtcNow));

    log.LogInformation("Sent payment {EndToEndId}: {Amount} {Currency} from {FromBsb} {FromAccount} to {Payee} ({ToBsb} {ToAccount}), reference {Reference}.",
        request.EndToEndId, request.Amount, request.Currency, request.Debtor?.Bsb, request.Debtor?.AccountNumber,
        request.Creditor?.Name, request.Creditor?.Bsb, request.Creditor?.AccountNumber, payment.NetworkReference);
    return Results.Created($"/v1/payments/{payment.NetworkReference}", payment);
});

// Administration: loopback only (the demo operator on this machine).
app.MapGet("/admin/behaviour", (HttpContext http) =>
    IsLocal(http) ? Results.Ok(state) : Results.NotFound());

app.MapPut("/admin/behaviour", (HttpContext http, BehaviourChange change) =>
{
    if (!IsLocal(http))
        return Results.NotFound();

    state = state with { Behaviour = change.Behaviour };
    log.LogWarning("Simulator behaviour changed: {Behaviour}.", state.Behaviour);
    return Results.Ok(state);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

// A refusal is final for that payment: repeating the request repeats the refusal.
IResult Refuse(string idempotencyKey, PaymentRequest request, string reason)
{
    log.LogWarning("Payment {EndToEndId}: REFUSED (422): {Reason}", request.EndToEndId, reason);
    return results.GetOrAdd(idempotencyKey, _ => Results.UnprocessableEntity(new { reason }));
}

// "Jane Citizen" -> "J Citizen": what a bank's record often looks like.
static string CloseMatchOf(string name)
{
    var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 2 ? $"{parts[0][0]} {string.Join(' ', parts[1..])}" : $"{name} Pty Ltd";
}

static bool IsLocal(HttpContext http) =>
    http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

enum Behaviour { Healthy, Slow, Failing, Down, Refusing }

sealed record SimulatorState(Behaviour Behaviour, int SlowDelaySeconds);

sealed record BehaviourChange(Behaviour Behaviour);

sealed record AccountParty(string? Bsb, string? AccountNumber, string? Name);

sealed record PaymentRequest(
    string? EndToEndId,
    AccountParty? Debtor,
    AccountParty? Creditor,
    decimal Amount,
    string? Currency,
    string? RemittanceInformation);

sealed record SentPayment(string NetworkReference, string Status, DateTimeOffset SettledAt);

sealed record PayeeConfirmationRequest(string? Bsb, string? AccountNumber, string? AccountName);

sealed record PayeeConfirmation(string Result, string? AccountNameHeld);

sealed record BsbEntry(string Bsb, string Bank, string Branch, string State, bool Npp);

/// <summary>
/// A tiny stand-in for the Australian BSB directory: the first two digits name the bank,
/// the third the state. Real enough for a demo; 999 is a deliberately "closed" bank.
/// </summary>
static class BsbDirectory
{
    private static readonly Dictionary<string, string> Banks = new()
    {
        ["01"] = "ANZ", ["03"] = "Westpac", ["06"] = "Commonwealth Bank", ["08"] = "National Australia Bank",
        ["11"] = "St.George Bank", ["18"] = "Macquarie Bank", ["48"] = "Suncorp Bank", ["63"] = "Bendigo Bank",
        ["73"] = "Westpac", ["80"] = "Cuscal (credit unions)", ["99"] = "Closed Bank (demo)"
    };

    private static readonly Dictionary<char, string> States = new()
    {
        ['2'] = "NSW", ['3'] = "VIC", ['4'] = "QLD", ['5'] = "SA", ['6'] = "WA", ['7'] = "TAS", ['0'] = "NT", ['1'] = "ACT"
    };

    public static BsbEntry? Find(string? bsb)
    {
        var digits = new string((bsb ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6 || !Banks.TryGetValue(digits[..2], out var bank))
            return null;

        var state = States.GetValueOrDefault(digits[2], "NSW");
        var formatted = $"{digits[..3]}-{digits[3..]}";
        return new BsbEntry(formatted, bank, $"{bank} branch {digits[3..]}, {state}", state, Npp: digits[..2] != "99");
    }
}