using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

// =============================================================================
// Core Banking Simulator - stands in for the bank's EXTERNAL core-banking system
// (the system of record that actually opens accounts), so the Accounts context can
// show how the platform survives a third party that is slow, failing, down or
// refusing (timeouts, retries, circuit breaker, and compensation after a failure).
//
// POST /v1/accounts              open an account (needs X-Api-Key and Idempotency-Key)
// GET  /admin/behaviour          current behaviour            } localhost only
// PUT  /admin/behaviour          change behaviour at runtime  }
//
// Behaviour: Healthy | Slow (answers after SlowDelaySeconds) | Failing (HTTP 500)
//            | Down (HTTP 503) | Refusing (HTTP 422: a permanent "no").
// Idempotency: the same Idempotency-Key always returns the SAME account, so a client
// may safely retry after a timeout without opening a second account.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
var apiKey = builder.Configuration["Simulator:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Simulator:ApiKey is not configured.");

var bsb = builder.Configuration["Simulator:Bsb"] ?? "062-000";
var state = new SimulatorState(
    Enum.Parse<Behaviour>(builder.Configuration["Simulator:Behaviour"] ?? nameof(Behaviour.Healthy), ignoreCase: true),
    builder.Configuration.GetValue("Simulator:SlowDelaySeconds", 12));

// Accounts opened so far, by idempotency key (in memory: a simulator, not a ledger).
var opened = new ConcurrentDictionary<string, OpenedAccount>(StringComparer.Ordinal);

// The account number is derived from the idempotency key (8 digits from its SHA-256), not from a
// counter: a counter restarts with the simulator and would hand out a number already used (the
// Accounts API's unique bsb + account number then refuses the new account), while the same key
// keeps getting the same account number - even across a restart, as a real system would.
static string AccountNumberFor(string idempotencyKey)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
    return (10_000_000UL + BitConverter.ToUInt64(hash, 0) % 90_000_000UL).ToString();
}

// Behaviour travels as text ("Down"), not as a number.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
var log = app.Logger;

app.MapPost("/v1/accounts", async (HttpContext http, OpenAccountRequest request, CancellationToken ct) =>
{
    // A real core-banking system authenticates its clients; constant-time key comparison.
    var supplied = http.Request.Headers["X-Api-Key"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(apiKey)))
        return Results.Unauthorized();

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
        return Results.BadRequest(new { reason = "An Idempotency-Key header is required." });

    // A repeated request returns the account already opened, whatever the behaviour:
    // the opening happened, only the answer was lost.
    if (opened.TryGetValue(idempotencyKey, out var existing))
    {
        log.LogInformation("Repeated request (key {Key}): returning account {Bsb} {Account} again.", idempotencyKey, existing.Bsb, existing.AccountNumber);
        return Results.Ok(existing);
    }

    var current = state;
    switch (current.Behaviour)
    {
        case Behaviour.Down:
            log.LogWarning("Open account for {Customer}: simulating an OUTAGE (503).", request.CustomerNumber);
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
        case Behaviour.Failing:
            log.LogWarning("Open account for {Customer}: simulating a FAILURE (500).", request.CustomerNumber);
            return Results.StatusCode((int)HttpStatusCode.InternalServerError);
        case Behaviour.Refusing:
            log.LogWarning("Open account for {Customer}: REFUSING (422).", request.CustomerNumber);
            return Results.UnprocessableEntity(new { reason = "Core banking refused the account: the customer's product eligibility check failed." });
        case Behaviour.Slow:
            log.LogWarning("Open account for {Customer}: simulating SLOWNESS ({Delay} s).", request.CustomerNumber, current.SlowDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(current.SlowDelaySeconds), ct);
            break;
    }

    var account = opened.GetOrAdd(idempotencyKey, _ => new OpenedAccount(
        AccountNumberFor(idempotencyKey),
        bsb,
        $"CBS-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
        request.Product ?? "EVERYDAY_TRANSACTION",
        DateTimeOffset.UtcNow));

    log.LogInformation("Opened account {Bsb} {Account} ({Product}) for {Customer} in the name of {AccountName}, branch {Branch}.",
        account.Bsb, account.AccountNumber, account.Product, request.CustomerNumber, request.AccountName, request.BranchCode);
    return Results.Created($"/v1/accounts/{account.AccountNumber}", account);
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

static bool IsLocal(HttpContext http) =>
    http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

enum Behaviour { Healthy, Slow, Failing, Down, Refusing }

sealed record SimulatorState(Behaviour Behaviour, int SlowDelaySeconds);

sealed record BehaviourChange(Behaviour Behaviour);

sealed record OpenAccountRequest(string CustomerNumber, string? AccountName, string? BranchCode, string? Product);

sealed record OpenedAccount(
    string AccountNumber,
    string Bsb,
    string Reference,
    string Product,
    DateTimeOffset OpenedAt);