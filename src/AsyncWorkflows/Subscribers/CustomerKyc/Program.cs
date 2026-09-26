using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Consumer;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.HostedServices;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaOptions>(
    builder.Configuration.GetSection(KafkaOptions.SectionName));

builder.Services.Configure<CustomerKycSubscriberOptions>(
    builder.Configuration.GetSection(CustomerKycSubscriberOptions.SectionName));

builder.Services.AddSingleton<IKafkaConsumer, KafkaConsumer>();
builder.Services.AddHostedService<CustomerKycSubscriberHostedService>();

var host = builder.Build();
await host.RunAsync();
