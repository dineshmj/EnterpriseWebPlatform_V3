using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.HostedServices;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Persistence;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Publishing;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaOptions>(
    builder.Configuration.GetSection(KafkaOptions.SectionName));

builder.Services.AddSingleton<IKafkaProducer, KafkaProducer>();

builder.Services.Configure<CustomerOutboxPublisherOptions>(
    builder.Configuration.GetSection(CustomerOutboxPublisherOptions.SectionName));

builder.Services.AddDbContext<CustomerOutboxDbContext>(options =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("CustomerDbConnection"));
});

builder.Services.AddScoped<ICustomerOutboxPublisher, CustomerOutboxPublisher>();

builder.Services.AddHostedService<CustomerOutboxPublisherHostedService>();

var host = builder.Build();

await host.RunAsync();