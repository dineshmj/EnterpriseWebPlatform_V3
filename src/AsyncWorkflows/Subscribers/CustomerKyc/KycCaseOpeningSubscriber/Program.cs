using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Processing;

var builder = Host.CreateApplicationBuilder(args);

// The shared reliable consume loop (commit after processing, retry in place,
// dead-letter) with this worker's processor.
builder.Services.AddKafkaSubscriber<KycCaseOpeningProcessor, KycCaseOpeningSubscriberOptions>(
    builder.Configuration, KycCaseOpeningSubscriberOptions.SectionName);

// The worker's own machine identity; the token is cached until shortly before expiry.
builder.Services
    .AddCachedM2MTokenClient<KycCaseOpeningSubscriberOptions>(KycCaseOpeningSubscriberOptions.SectionName)
    .AddStandardResilienceHandler();

builder.Services
    .AddHttpClient(KycCaseOpeningProcessor.HttpClientName, (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<KycCaseOpeningSubscriberOptions>>().Value;
        client.BaseAddress = new Uri(options.KycApiBaseUrl.TrimEnd('/'));
    })
    // Resilience pipeline around every call to the Customer KYC API:
    //  - attempt timeout     : a hung call is abandoned after 10 s
    //  - retry               : 3 retries, exponential back-off with jitter, on
    //                          network errors, timeouts, 408, 429 and 5xx. POST is
    //                          retried deliberately: the endpoint is idempotent
    //                          (Inbox + one case per application).
    //  - circuit breaker     : opens when half of at least 5 calls in 60 s fail, and
    //                          rejects calls for 30 s, giving the API room to recover
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

var host = builder.Build();
await host.RunAsync();
