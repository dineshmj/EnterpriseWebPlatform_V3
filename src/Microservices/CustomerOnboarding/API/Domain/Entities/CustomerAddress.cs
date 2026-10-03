using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;

/// <summary>An address of a customer. Part of the Customer aggregate; changed only through it.</summary>
public sealed class CustomerAddress : Entity
{
    // For EF Core materialization.
    private CustomerAddress()
    {
        Address = null!;
    }

    private CustomerAddress(
        AddressType addressType,
        PostalAddress address,
        bool isPrimary,
        DateTimeOffset now)
    {
        AddressType = addressType;
        Address = address;
        IsPrimary = isPrimary;

        CreatedAt = now;
        UpdatedAt = now;
    }

    public long CustomerId { get; private set; }

    public AddressType AddressType { get; private set; }

    public PostalAddress Address { get; private set; }

    public bool IsPrimary { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static CustomerAddress Create(
        AddressType addressType,
        PostalAddress address,
        bool isPrimary,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new CustomerAddress(
            addressType,
            address,
            isPrimary,
            now);
    }

    internal void AssignToCustomer(long customerId, DateTimeOffset now)
    {
        if (customerId <= 0)
        {
            throw new DomainRuleViolationException(
                "A valid customer is required.");
        }

        CustomerId = customerId;
        UpdatedAt = now;
    }
}
