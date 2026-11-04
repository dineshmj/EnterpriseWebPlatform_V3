using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Support;

/// <summary>
/// The REAL payment network client with the API's own pipeline (PaymentNetworkRegistration),
/// pointed at a WireMock network - shared by the payment network fixtures.
/// </summary>
internal static class PaymentNetworkTestClient
{
    public const string ApiKey = "test-network-key";

    public static IPaymentNetwork To(string baseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new PaymentNetworkOptions { BaseUrl = baseUrl, ApiKey = ApiKey }));
        services.AddPaymentNetworkClient();
        WireMockNeighbour.ShortenWaits(services, PaymentNetworkClient.HttpClientName);
        return services.BuildServiceProvider().GetRequiredService<IPaymentNetwork>();
    }
}