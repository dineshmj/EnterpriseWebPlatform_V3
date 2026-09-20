using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;

public sealed class CustomerAddress : Entity
{
    private CustomerAddress()
    {
        Address = null!;
        Customer = null!;
    }

    private CustomerAddress(
        AddressType addressType,
        PostalAddress address,
        bool isPrimary)
    {
        AddressType = addressType;
        Address = address;
        IsPrimary = isPrimary;

        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public long CustomerId { get; private set; }

    public AddressType AddressType { get; private set; }

    public PostalAddress Address { get; private set; }

    public bool IsPrimary { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Aggregates.Customer Customer { get; private set; } = null!;

    public static CustomerAddress Create(
        AddressType addressType,
        PostalAddress address,
        bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new CustomerAddress(
            addressType,
            address,
            isPrimary);
    }

    internal void AssignToCustomer(long customerId)
    {
        if (customerId <= 0)
        {
            throw new DomainRuleViolationException(
                "A valid customer is required.");
        }

        CustomerId = customerId;
        Touch();
    }

    public void ChangeAddress(PostalAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        Address = address;
        Touch();
    }

    public void SetPrimary(bool isPrimary)
    {
        IsPrimary = isPrimary;
        Touch();
    }

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}