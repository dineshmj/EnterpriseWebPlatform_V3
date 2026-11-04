using System.Net;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Accounts.AccountsCommandSubscriber.Processing;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Payments.PaymentsSagaReplySubscriber.Processing;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support;

// ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
// │ The REAL couriers, with only their surroundings replaced: the API they call is the
// │ Pact mock server (HTTP contracts) or a recorder (message contracts). The machine
// │ token is left out on purpose - DPoP is proven by the subscriber infrastructure tests;
// │ a contract is about the shape of what is exchanged.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>The two couriers under test, pointed at a given API address.</summary>
internal static class Couriers
{
    public static AccountsCommandProcessor AccountsCommand(Uri accountsApi, HttpMessageHandler? handler = null) =>
        new(Factory(accountsApi, handler), TokenClient(), NullLogger<AccountsCommandProcessor>.Instance);

    public static PaymentsSagaReplyProcessor PaymentsSagaReply(Uri paymentsApi, HttpMessageHandler? handler = null) =>
        new(Factory(paymentsApi, handler), TokenClient(), NullLogger<PaymentsSagaReplyProcessor>.Instance);

    /// <summary>A message as the subscriber host hands it to the courier.</summary>
    public static ConsumedMessage Consumed(string topic, string value) =>
        new(topic, Partition: 0, Offset: 0, Key: null, value, new Dictionary<string, string>());

    private static IHttpClientFactory Factory(Uri baseAddress, HttpMessageHandler? handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => handler is null
                ? new HttpClient { BaseAddress = baseAddress }
                : new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress });
        return factory.Object;
    }

    private static CachedM2MTokenClient TokenClient() =>
        new(Mock.Of<IHttpClientFactory>(), Mock.Of<IM2MClientSettings>(),
            new MachineKeys(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDsa.Create(ECCurve.NamedCurves.nistP256), TimeProvider.System),
            NullLogger<CachedM2MTokenClient>.Instance);
}

/// <summary>Stands in for the API in a MESSAGE contract test: answers every call alike and keeps the bodies.</summary>
internal sealed class RecordingApi(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public static readonly Uri Address = new("http://api.test");

    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status) { Content = new StringContent("{}") };
    }
}