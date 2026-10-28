using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace EnterpriseWebPlatform.Tests.Neighbours;

// ┌─ :: ARCHITECTURE :: An external system, played by WireMock ────────────────────────────────────
// │ Why : An adapter's decisions (accepted / refused / unavailable, the same Idempotency-Key on a
// │       retry, no redirect followed) must be proven against every answer the external system
// │       can give - including the ones the simulators cannot be made to give on cue.
// │ How : A WireMock.Net server inside the test process stands in for the external system; the
// │       REAL adapter, registered with the API's own resilience pipeline (only its waits
// │       shortened), talks to it. Each test stages answers, then reads what arrived.
// │ Ref : PartnerProducts.Api.Tests/Support/PartnerBankStub.cs | WireMock.Net | tst/README.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>A WireMock server started for one test, with the helpers the adapter tests share.</summary>
public sealed class WireMockNeighbour : IDisposable
{
    /// <summary>How long one attempt waits in the tests (the API waits 5 s).</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);

    public WireMockServer Server { get; }

    public string Url => Server.Url!;

    public WireMockNeighbour()
    {
        Server = WireMockServer.Start();
        WarmUp();
    }

    /// <summary>Every request to this path answers with this status and body (optionally late).</summary>
    public WireMockNeighbour Answers(string method, string path, int status, object? body = null, TimeSpan? after = null)
    {
        Server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(ResponseFor(status, body, after));
        return this;
    }

    /// <summary>Requests to this path answer with these, one after the other; the last one stays.</summary>
    public WireMockNeighbour AnswersInTurn(string method, string path, params (int Status, object? Body)[] answers)
    {
        var scenario = $"{method} {path}";
        for (var i = 0; i < answers.Length; i++)
        {
            var mapping = Server.Given(Request.Create().WithPath(path).UsingMethod(method)).InScenario(scenario);
            if (i > 0) mapping = mapping.WhenStateIs($"answer {i}");
            if (i < answers.Length - 1) mapping = mapping.WillSetStateTo($"answer {i + 1}");
            mapping.RespondWith(ResponseFor(answers[i].Status, answers[i].Body, null));
        }
        return this;
    }

    /// <summary>Every request that reached this path, in order.</summary>
    public IReadOnlyList<ILogEntry> Received(string path) =>
        Server.LogEntries.Where(e => e.RequestMessage?.Path == path).OrderBy(e => e.RequestMessage!.DateTime).ToList();

    /// <summary>
    /// The requests on this path once there are `count` of them. WireMock logs a request when it
    /// has ANSWERED it, so a request held by a delay shows up only after that delay.
    /// </summary>
    public async Task<IReadOnlyList<ILogEntry>> ReceivedAsync(string path, int count, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (Received(path).Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        return Received(path);
    }

    /// <summary>The value of one request header (null when it was not sent).</summary>
    public static string? Header(ILogEntry entry, string name) =>
        entry.RequestMessage?.Headers is { } headers && headers.TryGetValue(name, out var values) ? values.SingleOrDefault() : null;

    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
    // │ The API's pipeline as registered - same retries, same breaker, same rules on what
    // │ is retried - with only its WAITS shortened (back-off, attempt timeout), so a test
    // │ of three late attempts takes seconds, not a minute.
    // └──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Shortens the waits of the standard resilience pipeline of this HTTP client.</summary>
    public static void ShortenWaits(IServiceCollection services, string httpClientName) =>
        services.Configure<HttpStandardResilienceOptions>($"{httpClientName}-standard", options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(20);
            options.AttemptTimeout.Timeout = AttemptTimeout;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
        });

    private static IResponseBuilder ResponseFor(int status, object? body, TimeSpan? after)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is string text) response = response.WithBody(text);
        else if (body is not null) response = response.WithBodyAsJson(body);
        if (after is { } delay) response = response.WithDelay(delay);
        return response;
    }

    // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
    // │ The first request a WireMock server answers in a process is slow (its pipeline warms up) -
    // │ longer than the shortened timeouts the tests use. One throw-away request first, then a clean log.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private void WarmUp()
    {
        using var warmUp = new HttpClient();
        warmUp.GetAsync($"{Url}/__warm-up").GetAwaiter().GetResult().Dispose();
        Server.ResetLogEntries();
    }

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }
}