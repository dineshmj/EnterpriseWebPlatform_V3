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
// GET  /admin/behaviour          current behaviour            } localhost only
// PUT  /admin/behaviour          change behaviour at runtime  }
//
// Behaviour: Healthy | Slow (answers after SlowDelaySeconds) | Failing (HTTP 500)
//            | Down (HTTP 503).
// Outcome:   deterministic from the customer number - last digit 9 → MATCH,
//            7 or 8 → POTENTIAL_MATCH, otherwise CLEAR - unless ForcedOutcome is set.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
var apiKey = builder.Configuration["Simulator:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Simulator:ApiKey is not configured.");

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

    log.LogInformation("Screened {Customer} ({Application}): {Outcome}.", request.CustomerNumber, request.ApplicationNumber, outcome);
    return Results.Ok(response);
});

// Administration: loopback only (the demo operator on this machine).
app.MapGet("/admin/behaviour", (HttpContext http) =>
    IsLocal(http) ? Results.Ok(state) : Results.NotFound());

app.MapPut("/admin/behaviour", (HttpContext http, BehaviourChange change) =>
{
    if (!IsLocal(http))
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

static bool IsLocal(HttpContext http) =>
    http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

enum Behaviour { Healthy, Slow, Failing, Down }

sealed record SimulatorState(Behaviour Behaviour, string? ForcedOutcome, int SlowDelaySeconds);

sealed record BehaviourChange(Behaviour Behaviour, string? ForcedOutcome);

sealed record ScreeningRequest(string CustomerNumber, string ApplicationNumber, Guid RequestId, string[]? Lists);

sealed record ScreeningMatch(string List, double Score);

sealed record ScreeningResponse(
    string ScreeningId,
    string Provider,
    string Outcome,
    string[] ListsChecked,
    ScreeningMatch[] Matches,
    DateTimeOffset ScreenedAt);