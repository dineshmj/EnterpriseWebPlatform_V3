using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

public sealed record CustomerNumber
{
    public string Value { get; }

    private CustomerNumber(string value) => Value = value;

    public static CustomerNumber Create(long sequenceNumber)
    {
        if (sequenceNumber <= 0)
        {
            throw new DomainRuleViolationException(
                "Customer number sequence value must be greater than zero.");
        }

        var value = $"CUST-{sequenceNumber:D6}";

        if (value.Length > 30)
        {
            throw new DomainRuleViolationException(
                "Customer number cannot exceed 30 characters.");
        }

        return new CustomerNumber(value);
    }

    public override string ToString() => Value;
}
