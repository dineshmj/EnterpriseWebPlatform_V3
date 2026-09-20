using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddCustomerOnboardingApplication(
        this IServiceCollection services)
    {
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
            Onboarding.Queries.GetApplication.GetOnboardingApplicationQueryHandler>();

        services.AddScoped<
            Onboarding.Queries.GetApplications.GetOnboardingApplicationsQueryHandler>();

        return services;
    }
}