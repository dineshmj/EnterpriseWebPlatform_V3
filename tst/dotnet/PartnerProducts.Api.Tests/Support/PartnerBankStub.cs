using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

using EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Tests.Support;

// ┌─ :: ARCHITECTURE :: The Partner Bank, played by WireMock ──────────────────────────────────────
// │ Why : The adapter's decisions (Accepted / Refused / Unavailable, the signed instruction, the
// │       same jti on a resend) must be proven against every answer the Partner Bank can give -
// │       including the ones the real Partner Bank and the simulators cannot be made to give on cue.
// │ How : A WireMock server inside the test process stands in for the Partner Bank's token endpoint
// │       and distributor API; the REAL PartnerBankClient talks to it over plain HTTP. Each test
// │       stages answers, then reads what arrived (WireMock records every request). Throw-away
// │       keys are made per test run in a temporary folder - never the .ewp-pki ones.
// │       Not covered here: mutual TLS itself (the client certificate on the wire) - that is the
// │       Partner Bank's own check, proven in its repository and in the live runs.
// │ Ref : PartnerBankClient.cs | WireMock.Net | tst/README.md (.NET tests)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>A WireMock Partner Bank plus a real <see cref="PartnerBankClient"/> pointed at it.</summary>
public sealed class PartnerBankStub : IDisposable
{
    public const string ClientId = "EWP.PartnerProducts.Test.ClientID";
    public const string TokenPath = "/connect/token";
    public const string GoldOrdersPath = "/api/distribution/gold-orders";

    private static readonly Lazy<TestKeys> Keys = new(TestKeys.Create);

    public WireMockServer Server { get; }

    public PartnerBankClient Client { get; }

    /// <summary>The certificate whose private key signs every instruction (its public key verifies them).</summary>
    public static X509Certificate2 SigningCertificate => Keys.Value.Signing;

    /// <param name="timeout">How long the adapter waits for an answer (the API's PartnerBank:TimeoutSeconds, shortened).</param>
    public PartnerBankStub(TimeSpan? timeout = null)
    {
        ForgetCachedToken();
        Server = WireMockServer.Start();
        WarmUp();

        var options = new PartnerBankOptions
        {
            TokenEndpoint = $"{Server.Url}{TokenPath}",
            ApiBaseUrl = Server.Url!,
            ClientId = ClientId,
            ClientCertificatePath = Keys.Value.ClientPfx,
            ClientCertificatePasswordPath = Keys.Value.PasswordFile,
            SigningCertificatePath = Keys.Value.SigningPfx,
            SigningCertificatePasswordPath = Keys.Value.PasswordFile,
            TrustRootPath = Keys.Value.TrustRoot
        };
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        Client = new PartnerBankClient(http, options, new PartnerBankCredentials(options), TimeProvider.System, NullLogger<PartnerBankClient>.Instance);
    }

    /// <summary>The token endpoint issues EWP's token (client credentials).</summary>
    public PartnerBankStub IssuesTokens(string accessToken = "partner-token-1")
    {
        Server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { access_token = accessToken, token_type = "Bearer", expires_in = 300 }));
        return this;
    }

    /// <summary>The distributor API answers a purchase instruction with this status and body (optionally late).</summary>
    public PartnerBankStub AnswersPurchase(int status, object? body = null, TimeSpan? after = null)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null) response = response.WithBodyAsJson(body);
        if (after is { } delay) response = response.WithDelay(delay);
        Server.Given(Request.Create().WithPath(GoldOrdersPath).UsingPost()).RespondWith(response);
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

    // The first request a WireMock server answers in a process is slow (its pipeline warms up) -
    // longer than the shortened timeouts some tests use. One throw-away request first, then a clean log.
    private void WarmUp()
    {
        using var warmUp = new HttpClient();
        warmUp.GetAsync($"{Server.Url}/__warm-up").GetAwaiter().GetResult().Dispose();
        Server.ResetLogEntries();
    }

    /// <summary>A purchase instruction as the application builds it (the signed JWT carries it as a claim).</summary>
    public static JsonObject PurchaseInstruction(string orderReference = "DGP-261021-TEST01") => new()
    {
        ["type"] = "gold_purchase",
        ["ewpReference"] = orderReference,
        ["accountNumber"] = "PB-DGA-TEST01",
        ["amount"] = 20.00m,
        ["currency"] = "AUD"
    };

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The adapter keeps EWP's token in a STATIC cache (one per process, shared by every
    // │ HttpClient instance). Each test starts with an empty cache, so no test depends on
    // │ which one ran before it.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static void ForgetCachedToken() =>
        typeof(PartnerBankClient).GetField("cachedToken", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);

    /// <summary>Throw-away keys in a temporary folder, made once per test run.</summary>
    private sealed record TestKeys(string ClientPfx, string SigningPfx, string PasswordFile, string TrustRoot, X509Certificate2 Signing)
    {
        private const string Password = "test-only";

        public static TestKeys Create()
        {
            var folder = Directory.CreateTempSubdirectory("ewp-partner-bank-tests-").FullName;
            var passwordFile = Path.Combine(folder, "password.txt");
            File.WriteAllText(passwordFile, Password);

            var signing = SelfSigned("CN=EWP request signing (test)");
            var client = SelfSigned("CN=EWP partner client (test)");
            var signingPfx = Path.Combine(folder, "signing.pfx");
            var clientPfx = Path.Combine(folder, "client.pfx");
            var trustRoot = Path.Combine(folder, "root.cer");
            File.WriteAllBytes(signingPfx, signing.Export(X509ContentType.Pkcs12, Password));
            File.WriteAllBytes(clientPfx, client.Export(X509ContentType.Pkcs12, Password));
            File.WriteAllBytes(trustRoot, client.Export(X509ContentType.Cert));

            return new TestKeys(clientPfx, signingPfx, passwordFile, trustRoot, signing);
        }

        private static X509Certificate2 SelfSigned(string subject)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        }
    }
}