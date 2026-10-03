using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    public static IServiceCollection AddCustomerOnboardingPersistence(
        this IServiceCollection services)
    {
        services.AddScoped<Messaging.WorkflowContextAccessor>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IOnboardingApplicationRepository, OnboardingApplicationRepository>();
        services.AddScoped<ICustomerNumberGenerator, CustomerNumberGenerator>();
        services.AddScoped<IApplicationNumberGenerator, ApplicationNumberGenerator>();

        services.AddScoped<IApplicationUnitOfWork>(
            provider => provider.GetRequiredService<CustomerDbContext>());

        services.AddScoped<ICustomerReadContext>(
            provider => provider.GetRequiredService<CustomerDbContext>());

        services.AddScoped<IOnboardingApplicationReadContext>(
            provider => provider.GetRequiredService<CustomerDbContext>());
        services.AddScoped<IInboxStore>(
            provider => provider.GetRequiredService<CustomerDbContext>());

        return services;
    }
}