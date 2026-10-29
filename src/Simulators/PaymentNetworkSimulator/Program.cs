using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

using EnterpriseWebPlatform.Simulators.PaymentNetwork;

// =============================================================================
// Payment Network Simulator - stands in for the EXTERNAL payment network (an NPP-style
// clearing service that moves the money to the payee's bank), so the Payments saga can
// show what happens when a third party is slow, failing, down or refusing: retries,
// circuit breaker, and compensation (releasing the reserved funds) after a failure.
//
// POST /v1/payments              send a payment (needs X-Api-Key and Idempotency-Key)
// GET  /v1/payments/by-idempotency-key/{key}   status enquiry: was this payment sent? (X-Api-Key)
// GET  /v1/bsb/{bsb}             BSB directory: bank, branch, state (needs X-Api-Key)
// POST /v1/payee-confirmations   Confirmation of Payee: does the name match the account? (X-Api-Key)
// POST /v1/fraud-scores          the network's fraud score for a payment about to be made (X-Api-Key)
// GET  /admin/behaviour          current behaviour            } localhost, or with X-Simulator-Admin-Key
// PUT  /admin/behaviour          change behaviour at runtime  }
// GET  /fraud/admin/behaviour    the fraud scoring's own behaviour (same rule)
// PUT  /fraud/admin/behaviour    change it at runtime
//
// Fraud scoring (deterministic, for demos): a payee account number ending in 13 is on the
// network's mule list (DECLINE); AUD 500 or more to a payee the customer never paid, or
// AUD 50,000 or more to anyone, is REVIEW; anything else ALLOW. Its behaviour is switched
// apart from the payments': Healthy | Slow (answers after FraudSlowDelayMilliseconds - past
// the Payments API's latency budget) | Failing (500) | Down (503).
//
// Behaviour: Healthy | Slow (answers after SlowDelaySeconds) | Failing (HTTP 500)
//            | Down (HTTP 503) | Refusing (HTTP 422: a permanent "no")
//            | LosingAnswers (the payment IS sent, but every answer is lost: HTTP 504 - only
//              the status enquiry tells the truth; the case reconciliation exists for).
// Whatever the behaviour, a payee BSB starting with 999 is refused ("account closed"),
// so one payment can show the compensation path without changing the behaviour; a BSB
// that is not in the directory is refused as well.
// Confirmation of Payee (deterministic, for demos): an account number ending in 0 is a
// NO_MATCH, one ending in 9 a CLOSE_MATCH (the bank holds a slightly different name),
// anything else a MATCH.
// Idempotency: the same Idempotency-Key always returns the SAME result, so a client may
// safely retry after a timeout without paying twice.
// =============================================================================

// ┌─ :: ARCHITECTURE + SECURITY :: A stand-in external system ─────────────────────────────────────
// │ Why : The platform must be shown coping with a third party that is slow, failing, down or
// │       refusing - without depending on a real provider.
// │ How : Same contract style as a real partner: an API key compared in constant time (no key = no
// │       start), an Idempotency-Key that always returns the same result, and a behaviour switch
// │       that answers loopback callers only (in a cluster: a configured admin key).
// │ Ref : Features 1.6.3, 1.7.4 | Threat-Model TB6 | CWE-208 (timing-safe comparison)
// └────────────────────────────────────────────────────────────────────────────────────────────────
// A friendly console window title (instead of the EXE's full path) on a local Windows run.
if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected) Console.Title = "Payment Network Simulator";

var builder = WebApplication.CreateBuilder(args);
var apiKey = builder.Configuration["Simulator:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Simulator:ApiKey is not configured.");

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Optional admin key: lets a caller on ANOTHER machine (the Operations Journey API in a
// │ cluster) use the behaviour switch by sending it in X-Simulator-Admin-Key. Unset = the
// │ switch answers loopback callers only, as on a developer's machine. Set it only where the
// │ switch may exist (Development / Test) - never with a real provider.
// └──────────────────────────────────────────────────────────────────────────────────────
var adminKey = builder.Configuration["Simulator:AdminKey"];

var state = new SimulatorState(
    Enum.Parse<Behaviour>(builder.Configuration["Simulator:Behaviour"] ?? nameof(Behaviour.Healthy), ignoreCase: true),
    builder.Configuration.GetValue("Simulator:SlowDelaySeconds", 12));

// The fraud scoring is another service of the network: switched on its own, so a demo can make
// scoring slow while payments still flow (the Payments API's fallback rules take over).
var fraudState = new FraudScoringState(
    Enum.Parse<FraudBehaviour>(builder.Configuration["Simulator:FraudBehaviour"] ?? nameof(FraudBehaviour.Healthy), ignoreCase: true),
    builder.Configuration.GetValue("Simulator:FraudSlowDelayMilliseconds", 2000));

// Payments sent (or refused) so far, by idempotency key (in memory: a simulator, not a clearing house).
var results = new ConcurrentDictionary<string, IResult>(StringComparer.Ordinal);
var sent = new ConcurrentDictionary<string, SentPayment>(StringComparer.Ordinal);
var refusedReasons = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

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

    log.LogInformation("Confirmation of Payee for {Bsb} {Account} \"{Name}\": {Result}.", request.Bsb, Masked(number), name, result.Result);
    return Results.Ok(result);
});

// ┌─ :: ARCHITECTURE :: The network's fraud score, asked before a payment is made ─────────────────
// │ Why : Payment schemes sell a risk score for a payment about to be made (the network sees
// │       every bank's traffic: mule accounts, unusual payees). The Payments API asks it within
// │       a latency budget, and must cope when it is late or down.
// │ How : A score 0-999, a decision ALLOW / REVIEW / DECLINE and the reasons, from fixed demo
// │       rules (see the header); its own behaviour switch (/fraud/admin/behaviour) makes it
// │       slow, failing or down while payments still flow.
// │ Ref : Payments API README (fraud check) | Features 1.4 | e.g. Mastercard Consumer Fraud Risk
// └────────────────────────────────────────────────────────────────────────────────────────────────
app.MapPost("/v1/fraud-scores", async (HttpContext http, FraudScoreRequest request) =>
{
    if (!Authorised(http))
        return Results.Unauthorized();

    var current = fraudState;
    switch (current.Behaviour)
    {
        case FraudBehaviour.Down:
            log.LogWarning("Fraud score {EndToEndId}: simulating an OUTAGE (503).", request.EndToEndId);
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
        case FraudBehaviour.Failing:
            log.LogWarning("Fraud score {EndToEndId}: simulating a FAILURE (500).", request.EndToEndId);
            return Results.StatusCode((int)HttpStatusCode.InternalServerError);
        case FraudBehaviour.Slow:
            log.LogWarning("Fraud score {EndToEndId}: simulating SLOWNESS ({Delay} ms).", request.EndToEndId, current.SlowDelayMilliseconds);
            await Task.Delay(current.SlowDelayMilliseconds, CancellationToken.None);
            break;
    }

    var score = FraudRules.Score(request);
    log.LogInformation("Fraud score {EndToEndId}: {Amount} {Currency} to {ToBsb} {ToAccount} (first payment to this payee: {FirstPayment}) -> {Score} {Decision} [{Reasons}].",
        request.EndToEndId, request.Amount, request.Currency, request.Creditor?.Bsb, Masked(request.Creditor?.AccountNumber),
        request.FirstPaymentToPayee, score.Score, score.Decision, string.Join(", ", score.Reasons));
    return Results.Ok(score);
});

app.MapPost("/v1/payments", async (HttpContext http, PaymentRequest request, CancellationToken ct) =>
{
    if (!Authorised(http))
        return Results.Unauthorized();

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
        return Results.BadRequest(new { reason = "An Idempotency-Key header is required." });

    // LosingAnswers: the payment goes out (once - the key decides), but no answer comes back,
    // not even to a repeated request. The sender can only learn the truth from the status enquiry.
    if (state.Behaviour == Behaviour.LosingAnswers && !results.ContainsKey(idempotencyKey) && BsbDirectory.Find(request.Creditor?.Bsb) is { Npp: true })
    {
        var lost = sent.GetOrAdd(idempotencyKey, _ => new SentPayment(
            $"NPP{DateTimeOffset.UtcNow:yyMMdd}{Convert.ToHexString(RandomNumberGenerator.GetBytes(5))}", "SETTLED", DateTimeOffset.UtcNow));
        log.LogWarning("Payment {EndToEndId}: SENT as {Reference}, but the answer is LOST (504).", request.EndToEndId, lost.NetworkReference);
        return Results.StatusCode((int)HttpStatusCode.GatewayTimeout);
    }

    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
    // │ A repeated request returns the first answer, whatever the behaviour now: the payment
    // │ was already sent (or refused); only the answer was lost.
    // └──────────────────────────────────────────────────────────────────────────────────────
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
            // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
            // │ A slow network still PROCESSES the payment after the caller gave up waiting: the
            // │ delay ignores the request's cancellation, so the payment is recorded and a resend
            // │ (same Idempotency-Key) or the status enquiry finds it SENT - the unknown outcome the
            // │ saga must reconcile, never pay twice. (Honouring the cancellation made "Slow" a
            // │ network that silently drops every slow payment.)
            // └──────────────────────────────────────────────────────────────────────────────────────
            await Task.Delay(TimeSpan.FromSeconds(current.SlowDelaySeconds), CancellationToken.None);
            break;
    }

    var payment = sent.GetOrAdd(idempotencyKey, _ => new SentPayment(
        $"NPP{DateTimeOffset.UtcNow:yyMMdd}{Convert.ToHexString(RandomNumberGenerator.GetBytes(5))}",
        "SETTLED",
        DateTimeOffset.UtcNow));

    log.LogInformation("Sent payment {EndToEndId}: {Amount} {Currency} from {FromBsb} {FromAccount} to {Payee} ({ToBsb} {ToAccount}), reference {Reference}.",
        request.EndToEndId, request.Amount, request.Currency, request.Debtor?.Bsb, Masked(request.Debtor?.AccountNumber),
        request.Creditor?.Name, request.Creditor?.Bsb, Masked(request.Creditor?.AccountNumber), payment.NetworkReference);
    return Results.Created($"/v1/payments/{payment.NetworkReference}", payment);
});

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ Status enquiry by the sender's Idempotency-Key - how a participant reconciles an unknown
// │ outcome before deciding anything. It answers while the network is merely losing answers; when
// │ the network is down or failing, so is the enquiry.
// └──────────────────────────────────────────────────────────────────────────────────────
app.MapGet("/v1/payments/by-idempotency-key/{idempotencyKey}", (HttpContext http, string idempotencyKey) =>
{
    if (!Authorised(http))
        return Results.Unauthorized();
    if (state.Behaviour is Behaviour.Down)
        return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
    if (state.Behaviour is Behaviour.Failing)
        return Results.StatusCode((int)HttpStatusCode.InternalServerError);

    if (sent.TryGetValue(idempotencyKey, out var payment))
    {
        log.LogInformation("Status enquiry {Key}: SENT ({Reference}).", idempotencyKey, payment.NetworkReference);
        return Results.Ok(new { status = "SENT", networkReference = payment.NetworkReference });
    }
    if (refusedReasons.TryGetValue(idempotencyKey, out var reason))
        return Results.Ok(new { status = "REFUSED", reason });

    log.LogInformation("Status enquiry {Key}: never received.", idempotencyKey);
    return Results.NotFound(new { status = "NOT_FOUND" });
});

// Administration: loopback, or the configured admin key (the Operations Journey API in a cluster).
app.MapGet("/admin/behaviour", (HttpContext http) =>
    IsAdmin(http) ? Results.Ok(state) : Results.NotFound());

app.MapPut("/admin/behaviour", (HttpContext http, BehaviourChange change) =>
{
    if (!IsAdmin(http))
        return Results.NotFound();

    state = state with { Behaviour = change.Behaviour };
    log.LogWarning("Simulator behaviour changed: {Behaviour}.", state.Behaviour);
    return Results.Ok(state);
});

// The fraud scoring's own switch, same rule (the Operations screen lists it as its own simulator).
app.MapGet("/fraud/admin/behaviour", (HttpContext http) =>
    IsAdmin(http) ? Results.Ok(fraudState) : Results.NotFound());

app.MapPut("/fraud/admin/behaviour", (HttpContext http, FraudBehaviourChange change) =>
{
    if (!IsAdmin(http))
        return Results.NotFound();

    fraudState = fraudState with { Behaviour = change.Behaviour };
    log.LogWarning("Fraud scoring behaviour changed: {Behaviour}.", fraudState.Behaviour);
    return Results.Ok(fraudState);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

await app.RunAsync();

// A refusal is final for that payment: repeating the request repeats the refusal.
IResult Refuse(string idempotencyKey, PaymentRequest request, string reason)
{
    log.LogWarning("Payment {EndToEndId}: REFUSED (422): {Reason}", request.EndToEndId, reason);
    refusedReasons.TryAdd(idempotencyKey, reason);
    return results.GetOrAdd(idempotencyKey, _ => Results.UnprocessableEntity(new { reason }));
}

// "Jane Citizen" -> "J Citizen": what a bank's record often looks like.
static string CloseMatchOf(string name)
{
    var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 2 ? $"{parts[0][0]} {string.Join(' ', parts[1..])}" : $"{name} Pty Ltd";
}

bool IsAdmin(HttpContext http) =>
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The behaviour switch: a caller on this machine, or one that sends the configured admin
    // │ key (compared in constant time). No key configured = loopback only.
    // └──────────────────────────────────────────────────────────────────────────────────────
    IsLocal(http) || (!string.IsNullOrEmpty(adminKey) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(http.Request.Headers["X-Simulator-Admin-Key"].ToString()), Encoding.UTF8.GetBytes(adminKey)));

static bool IsLocal(HttpContext http) =>
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The behaviour switch answers only callers on this machine (loopback) - nobody on the
    // │ network can switch the provider off.
    // └──────────────────────────────────────────────────────────────────────────────────────
    http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

// Logs show the last four digits only, like every platform component (LogMasking).
static string Masked(string? accountNumber)
{
    var value = accountNumber?.Trim() ?? string.Empty;
    return value.Length <= 4 ? new string('*', value.Length) : $"****{value[^4..]}";
}