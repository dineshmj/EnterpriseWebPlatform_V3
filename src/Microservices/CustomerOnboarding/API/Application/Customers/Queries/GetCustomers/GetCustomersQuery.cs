namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomers;

public sealed record GetCustomersQuery(
    int PageNumber = 1,
    int PageSize = 25,
    string? Search = null);