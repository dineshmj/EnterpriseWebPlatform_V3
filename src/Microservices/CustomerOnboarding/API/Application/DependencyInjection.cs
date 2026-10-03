namespace EnterpriseWebPlatform.CustomerOnboarding.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddCustomerOnboardingApplication(
        this IServiceCollection services)
    {
        // The domain never reads the system clock itself; handlers pass "now" in.
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<
            Customers.Commands.CreateCustomer.CreateCustomerCommandHandler>();

        services.AddScoped<
            Customers.Commands.UpdateCustomer.UpdateCustomerCommandHandler>();

        services.AddScoped<
            Customers.Queries.GetCustomer.GetCustomerQueryHandler>();

        services.AddScoped<
            Customers.Queries.GetCustomers.GetCustomersQueryHandler>();

        services.AddScoped<
            Onboarding.Commands.CreateApplication.CreateOnboardingApplicationCommandHandler>();

        services.AddScoped<
            Onboarding.Commands.SubmitApplication.SubmitOnboardingApplicationCommandHandler>();
        services.AddScoped<
            Onboarding.Commands.RecordKycOutcome.RecordKycOutcomeCommandHandler>();

        services.AddScoped<
            Onboarding.Queries.GetApplication.GetOnboardingApplicationQueryHandler>();

        services.AddScoped<
            Onboarding.Queries.GetApplications.GetOnboardingApplicationsQueryHandler>();

        return services;
    }
}