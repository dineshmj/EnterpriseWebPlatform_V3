using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Authentication;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.HostedServices;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Processing;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaOptions>(
    builder.Configuration.GetSection(KafkaOptions.SectionName));

builder.Services
    .AddOptions<KycSubscriberOptions>()
    .Bind(builder.Configuration.GetSection(KycSubscriberOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret), "CustomerOnboardingKycSubscriber:ClientSecret is not configured.")
    .Validate(o => o.Topics.Length > 0, "CustomerOnboardingKycSubscriber:Topics is empty.")
    .ValidateOnStart();

builder.Services
    .AddHttpClient("IdentityServer", (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<KycSubscriberOptions>>().Value;
        client.BaseAddress = new Uri(options.IdentityServerAuthority.TrimEnd('/'));
    })
    .AddStandardResilienceHandler();

builder.Services
    .AddHttpClient("CustomerOnboardingApi", (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<KycSubscriberOptions>>().Value;
        client.BaseAddress = new Uri(options.CustomerOnboardingApiBaseUrl.TrimEnd('/'));
    })
    // Resilience pipeline around every call to the Customer Onboarding API:
    //  - attempt timeout     : a hung call is abandoned after 10 s
    //  - retry               : 3 retries, exponential back-off with jitter, on
    //                          network errors, timeouts, 408, 429 and 5xx. POST is
    //                          retried deliberately: the endpoint is idempotent per
    //                          MessageId (Inbox), so a retry cannot apply twice.
    //  - circuit breaker     : opens when half of at least 5 calls in 60 s fail, and
    //                          rejects calls for 30 s, giving the API room to recover
    //                          instead of being hammered by every retry
    //  - total timeout       : 60 s for the whole sequence
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.UseJitter = true;
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
    });

builder.Services.AddSingleton<IKafkaProducer, KafkaProducer>();
builder.Services.AddSingleton<CachedM2MTokenClient>();
builder.Services.AddSingleton<KycOutcomeProcessor>();
builder.Services.AddHostedService<KycOutcomeConsumerHostedService>();

var host = builder.Build();
await host.RunAsync();
