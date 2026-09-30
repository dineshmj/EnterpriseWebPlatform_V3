using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Authentication;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Clients;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Consumer;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.HostedServices;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaOptions>(
    builder.Configuration.GetSection(KafkaOptions.SectionName));

builder.Services.Configure<CustomerKycSubscriberOptions>(
    builder.Configuration.GetSection(CustomerKycSubscriberOptions.SectionName));

builder.Services.AddHttpClient("IdentityServer", (serviceProvider, client) =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CustomerKycSubscriberOptions>>()
        .Value;

    client.BaseAddress = new Uri(options.IdentityServerAuthority.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
});

builder.Services.AddHttpClient("CustomerKycApi", (serviceProvider, client) =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CustomerKycSubscriberOptions>>()
        .Value;

    client.BaseAddress = new Uri(options.KycApiBaseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
});

builder.Services.AddSingleton<IKafkaConsumer, KafkaConsumer>();
builder.Services.AddSingleton<M2MTokenClient>();
builder.Services.AddSingleton<CustomerKycApiClient>();
builder.Services.AddHostedService<CustomerKycSubscriberHostedService>();

var host = builder.Build();
await host.RunAsync();