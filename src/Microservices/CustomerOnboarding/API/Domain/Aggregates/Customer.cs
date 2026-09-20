using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

public sealed class Customer : AggregateRoot
{
    private readonly List<CustomerAddress> _addresses = [];

    private Customer()
    {
        CustomerNumber = null!; Email = null!; PhoneNumber = null!;
    }

    private Customer(CustomerNumber customerNumber, string firstName, string lastName,
        EmailAddress email, PhoneNumber phoneNumber, CustomerType customerType,
        Guid? subjectId, long? branchId)
    {
        CustomerNumber = customerNumber;
        FirstName = RequireName(firstName, nameof(firstName));
        LastName = RequireName(lastName, nameof(lastName));
        Email = email; PhoneNumber = phoneNumber; CustomerType = customerType;
        SubjectId = subjectId; BranchId = branchId;
        Status = CustomerStatus.Prospect;
        CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = CreatedAt; Version = 1;
    }

    public CustomerNumber CustomerNumber { get; private set; }
    public Guid? SubjectId { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public EmailAddress Email { get; private set; }
    public PhoneNumber PhoneNumber { get; private set; }
    public CustomerType CustomerType { get; private set; }
    public CustomerStatus Status { get; private set; }
    public long? BranchId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    public IReadOnlyCollection<CustomerAddress> Addresses => _addresses.AsReadOnly();

    public static Customer Create(CustomerNumber customerNumber, string firstName, string lastName,
        EmailAddress email, PhoneNumber phoneNumber, CustomerType customerType,
        Guid? subjectId = null, long? branchId = null)
    {
        ArgumentNullException.ThrowIfNull(customerNumber);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(phoneNumber);

        var customer = new Customer(customerNumber, firstName, lastName, email, phoneNumber,
            customerType, subjectId, branchId);

        customer.RaiseDomainEvent(new CustomerCreatedDomainEvent(
            customer.Id, customer.CustomerNumber.Value, customer.CreatedAt));

        return customer;
    }

    public void AddAddress(Entities.CustomerAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsPrimary && _addresses.Any(x => x.IsPrimary))
            throw new DomainRuleViolationException("A customer cannot have more than one primary address.");

        _addresses.Add(address);
        address.AssignToCustomer(Id);
        Touch();
    }

    public void ChangeContactDetails(EmailAddress email, PhoneNumber phoneNumber)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(phoneNumber);
        Email = email; PhoneNumber = phoneNumber; Touch();
    }

    public void StartOnboarding()
    {
        if (Status is CustomerStatus.Closed or CustomerStatus.Suspended)
            throw new DomainRuleViolationException("A closed or suspended customer cannot start onboarding.");
        Status = CustomerStatus.Onboarding; Touch();
    }

    public void Activate()
    {
        if (Status != CustomerStatus.Onboarding)
            throw new DomainRuleViolationException("Only a customer in onboarding can be activated.");
        Status = CustomerStatus.Active; Touch();
    }

    public void Suspend()
    {
        if (Status == CustomerStatus.Closed)
            throw new DomainRuleViolationException("A closed customer cannot be suspended.");
        Status = CustomerStatus.Suspended; Touch();
    }

    public void UpdateName(string firstName, string lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
        {
            throw new DomainRuleViolationException(
                "First name is required.");
        }

        if (string.IsNullOrWhiteSpace(lastName))
        {
            throw new DomainRuleViolationException(
                "Last name is required.");
        }

        FirstName = firstName.Trim();
        LastName = lastName.Trim();

        Touch();
    }

    private void Touch() { UpdatedAt = DateTimeOffset.UtcNow; Version++; }

    private static string RequireName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException($"{parameterName} is required.");

        var normalized = value.Trim();

        if (normalized.Length > 100)
            throw new DomainRuleViolationException($"{parameterName} cannot exceed 100 characters.");

        return normalized;
    }
}