using EnterpriseWebPlatform.CustomerOnboarding.API.Bian;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;

/// <summary>A customer as /v2 shows it, and BIAN contact points - shared by the /v2 fixtures.</summary>
internal static class BianExamples
{
    public static BianContactPoint Contact(string type, string address) => new()
    {
        ContactPointType = type,
        LocationReference = new BianLocation { LocationAddress = address }
    };

    public static CustomerDetailsDto Customer() => new(
        CustomerId: 42,
        CustomerNumber: "CUS-000123",
        FirstName: "Camilla",
        LastName: "Parker",
        Email: "camilla.parker@example.com",
        PhoneNumber: "+61 412 345 678",
        CustomerType: CustomerType.Individual,
        Status: "ACTIVE",
        BranchId: 1,
        Version: 3,
        ResidentialAddress: new CustomerAddressDto("1 George Street", "Level 4", "Sydney", "NSW", "2000", "AU"),
        CreatedAt: new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero));
}