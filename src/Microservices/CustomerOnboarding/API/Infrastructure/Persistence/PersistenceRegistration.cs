using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Privacy;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    public static IServiceCollection AddCustomerOnboardingPersistence(
        this IServiceCollection services)
    {
        services.AddScoped<Messaging.WorkflowContextAccessor>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IStaffDirectory, StaffDirectory>();
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

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Crypto-shredding: the master keys come from configuration "PersonalData" (Key Vault
        // │ outside Development); a missing, short or reused key stops the API at start-up
        // │ instead of writing personal data unprotected.
        // └──────────────────────────────────────────────────────────────────────────────────────
        services.AddOptions<PersonalDataProtectionOptions>()
            .BindConfiguration(PersonalDataProtectionOptions.SectionName)
            .Validate(options => options.IsValid(),
                "PersonalData:MasterKey and PersonalData:BlindIndexKey must each be base64 of 32 random bytes, and differ.")
            .ValidateOnStart();
        services.AddSingleton(provider =>
            new PersonalDataCipher(provider.GetRequiredService<IOptions<PersonalDataProtectionOptions>>().Value));
        services.AddScoped<CustomerPersonalData>();
        services.AddScoped<ICustomerPersonalData>(provider => provider.GetRequiredService<CustomerPersonalData>());

        return services;
    }
}