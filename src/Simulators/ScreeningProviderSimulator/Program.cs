using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

// =============================================================================
// Screening Provider Simulator - stands in for an EXTERNAL AML / sanctions / PEP
// screening vendor, so the Compliance context can demonstrate how a platform
// survives a third party that is slow, failing or down (timeouts, retries,
// circuit breaker, "provider failure is never a pass").
//
// POST /v1/screenings            screen a customer (needs X-Api-Key)
// GET  /admin/behaviour          current behaviour            } localhost, or with X-Simulator-Admin-Key
// PUT  /admin/behaviour          change behaviour at runtime  }
//
// Behaviour: Healthy | Slow (answers after SlowDelaySeconds) | Failing (HTTP 500)
//            | Down (HTTP 503).
// Outcome:   deterministic from the customer number - last digit 9 → MATCH,
//            7 or 8 → POTENTIAL_MATCH, otherwise CLEAR - unless ForcedOutcome is set.
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
if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected) Console.Title = "Screening Provider Simulator";

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
    null,
    builder.Configuration.GetValue("Simulator:SlowDelaySeconds", 12));

// Behaviour travels as text ("Down"), not as a number.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
var log = app.Logger;

app.MapPost("/v1/screenings", async (HttpContext http, ScreeningRequest request, CancellationToken ct) =>
{
    // A real provider authenticates its clients; constant-time key comparison.
    var supplied = http.Request.Headers["X-Api-Key"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(apiKey)))
        return Results.Unauthorized();

    var current = state;
    switch (current.Behaviour)
    {
        case Behaviour.Down:
            log.LogWarning("Screening request for {Customer}: simulating an OUTAGE (503).", request.CustomerNumber);
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
        case Behaviour.Failing:
            log.LogWarning("Screening request for {Customer}: simulating a FAILURE (500).", request.CustomerNumber);
            return Results.StatusCode((int)HttpStatusCode.InternalServerError);
        case Behaviour.Slow:
            log.LogWarning("Screening request for {Customer}: simulating SLOWNESS ({Delay} s).", request.CustomerNumber, current.SlowDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(current.SlowDelaySeconds), ct);
            break;
    }

    var outcome = current.ForcedOutcome ?? OutcomeFor(request.CustomerNumber);
    var response = new ScreeningResponse(
        $"SCR-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
        "SimScreen AML/Sanctions/PEP",
        outcome,
        request.Lists ?? ["AML", "SANCTIONS", "PEP"],
        outcome == "CLEAR" ? [] : [new ScreeningMatch(outcome == "MATCH" ? "SANCTIONS" : "PEP", outcome == "MATCH" ? 0.97 : 0.71)],
        DateTimeOffset.UtcNow);

    log.LogInformation("Screened {Name} ({Country}) - {Customer}, {Application}: {Outcome}.",
        request.Subject is null ? "(no subject)" : $"{request.Subject.FirstName} {request.Subject.LastName}",
        request.Subject?.CountryCode ?? "-", request.CustomerNumber, request.ApplicationNumber, outcome);
    return Results.Ok(response);
});

// Administration: loopback, or the configured admin key (the Operations Journey API in a cluster).
app.MapGet("/admin/behaviour", (HttpContext http) =>
    IsAdmin(http) ? Results.Ok(state) : Results.NotFound());

app.MapPut("/admin/behaviour", (HttpContext http, BehaviourChange change) =>
{
    if (!IsAdmin(http))
        return Results.NotFound();
    if (change.ForcedOutcome is not (null or "CLEAR" or "POTENTIAL_MATCH" or "MATCH"))
        return Results.BadRequest(new { error = "ForcedOutcome must be CLEAR, POTENTIAL_MATCH, MATCH or null." });

    state = state with { Behaviour = change.Behaviour, ForcedOutcome = change.ForcedOutcome };
    log.LogWarning("Simulator behaviour changed: {Behaviour}, forced outcome {Outcome}.", state.Behaviour, state.ForcedOutcome ?? "none");
    return Results.Ok(state);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

static string OutcomeFor(string customerNumber)
{
    var last = customerNumber.LastOrDefault(char.IsDigit);
    return last switch { '9' => "MATCH", '7' or '8' => "POTENTIAL_MATCH", _ => "CLEAR" };
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

enum Behaviour { Healthy, Slow, Failing, Down }

sealed record SimulatorState(Behaviour Behaviour, string? ForcedOutcome, int SlowDelaySeconds);

sealed record BehaviourChange(Behaviour Behaviour, string? ForcedOutcome);

sealed record ScreeningRequest(string CustomerNumber, string ApplicationNumber, Guid RequestId, string[]? Lists, ScreeningSubject? Subject);

// Who is screened: real providers match the lists on name and address.
sealed record ScreeningSubject(string? FirstName, string? LastName, string? AddressLine1, string? City, string? State, string? PostalCode, string? CountryCode);

sealed record ScreeningMatch(string List, double Score);

sealed record ScreeningResponse(
    string ScreeningId,
    string Provider,
    string Outcome,
    string[] ListsChecked,
    ScreeningMatch[] Matches,
    DateTimeOffset ScreenedAt);