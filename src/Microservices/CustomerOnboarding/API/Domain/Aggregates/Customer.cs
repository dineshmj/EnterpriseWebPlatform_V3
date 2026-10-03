using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

/// <summary>
/// Aggregate root: a customer and their addresses.
///
/// Invariants:
///  - A customer always has a valid name, e-mail address and phone number.
///  - At most one address is primary.
///  - Lifecycle: PROSPECT → ONBOARDING → ACTIVE; SUSPENDED from any non-closed
///    status; a closed or suspended customer cannot start onboarding.
///  - Every customer has exactly one managing agent (ReBAC "manages" relationship,
///    owned by this context, not by the IDP).
/// </summary>
public sealed class Customer : AggregateRoot
{
    private readonly List<CustomerAddress> _addresses = [];

    // For EF Core materialization.
    private Customer()
    {
        CustomerNumber = null!;
        Name = null!;
        Email = null!;
        PhoneNumber = null!;
        ManagingAgentUserId = null!;
    }

    private Customer(
        CustomerNumber customerNumber,
        PersonName name,
        EmailAddress email,
        PhoneNumber phoneNumber,
        CustomerType customerType,
        string managingAgentUserId,
        Guid? subjectId,
        long? branchId,
        DateTimeOffset now)
    {
        CustomerNumber = customerNumber;
        Name = name;
        Email = email;
        PhoneNumber = phoneNumber;
        CustomerType = customerType;
        ManagingAgentUserId = managingAgentUserId;
        SubjectId = subjectId;
        BranchId = branchId;
        Status = CustomerStatus.Prospect;
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    public CustomerNumber CustomerNumber { get; private set; }

    public Guid? SubjectId { get; private set; }

    public PersonName Name { get; private set; }

    public EmailAddress Email { get; private set; }

    public PhoneNumber PhoneNumber { get; private set; }

    public CustomerType CustomerType { get; private set; }

    public CustomerStatus Status { get; private set; }

    public long? BranchId { get; private set; }

    /// <summary>IDP subject of the agent who manages this customer.</summary>
    public string ManagingAgentUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyCollection<CustomerAddress> Addresses =>
        _addresses.AsReadOnly();

    public static Customer Create(
        CustomerNumber customerNumber,
        PersonName name,
        EmailAddress email,
        PhoneNumber phoneNumber,
        CustomerType customerType,
        string managingAgentUserId,
        DateTimeOffset now,
        Guid? subjectId = null,
        long? branchId = null)
    {
        ArgumentNullException.ThrowIfNull(customerNumber);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(phoneNumber);

        if (string.IsNullOrWhiteSpace(managingAgentUserId))
        {
            throw new DomainRuleViolationException(
                "A customer must have a managing agent.");
        }

        var customer = new Customer(
            customerNumber,
            name,
            email,
            phoneNumber,
            customerType,
            managingAgentUserId.Trim(),
            subjectId,
            branchId,
            now);

        customer.RaiseDomainEvent(
            new CustomerCreatedDomainEvent(
                customer.CustomerNumber.Value,
                now));

        return customer;
    }

    /// <summary>ReBAC: is <paramref name="userId"/> the agent who manages this customer?</summary>
    public bool IsManagedBy(string? userId) =>
        !string.IsNullOrWhiteSpace(userId) &&
        string.Equals(ManagingAgentUserId, userId.Trim(), StringComparison.OrdinalIgnoreCase);

    public void AddAddress(CustomerAddress address, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsPrimary && _addresses.Any(x => x.IsPrimary))
        {
            throw new DomainRuleViolationException(
                "A customer cannot have more than one primary address.");
        }

        _addresses.Add(address);

        // A new customer has no database ID yet; persistence links the address
        // through the aggregate's relationship when the customer is saved.
        if (Id > 0)
        {
            address.AssignToCustomer(Id, now);
        }

        Touch(now);
    }

    public void ChangeContactDetails(
        EmailAddress email,
        PhoneNumber phoneNumber,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(phoneNumber);

        if (Email == email && PhoneNumber == phoneNumber)
        {
            return;
        }

        Email = email;
        PhoneNumber = phoneNumber;
        Touch(now);

        RaiseDomainEvent(new CustomerContactDetailsChangedDomainEvent(Id, now));
    }

    public void Rename(PersonName name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Name == name)
        {
            return;
        }

        Name = name;
        Touch(now);
    }

    public void StartOnboarding(DateTimeOffset now)
    {
        if (Status is CustomerStatus.Closed or CustomerStatus.Suspended)
        {
            throw new DomainRuleViolationException(
                "A closed or suspended customer cannot start onboarding.");
        }

        ChangeStatus(CustomerStatus.Onboarding, now);
    }

    public void Activate(DateTimeOffset now)
    {
        if (Status != CustomerStatus.Onboarding)
        {
            throw new DomainRuleViolationException(
                "Only a customer in onboarding can be activated.");
        }

        ChangeStatus(CustomerStatus.Active, now);
    }

    public void Suspend(DateTimeOffset now)
    {
        if (Status == CustomerStatus.Closed)
        {
            throw new DomainRuleViolationException(
                "A closed customer cannot be suspended.");
        }

        ChangeStatus(CustomerStatus.Suspended, now);
    }

    private void ChangeStatus(CustomerStatus target, DateTimeOffset now)
    {
        if (Status == target)
        {
            return;
        }

        var previous = Status;
        Status = target;
        Touch(now);

        RaiseDomainEvent(new CustomerStatusChangedDomainEvent(Id, previous, target, now));
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}
