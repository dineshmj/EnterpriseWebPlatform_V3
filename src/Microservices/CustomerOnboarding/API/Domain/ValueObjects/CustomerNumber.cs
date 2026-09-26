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

    public static CustomerNumber FromValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainRuleViolationException(
                "Customer number cannot be empty.");
        }

        value = value.Trim();

        if (!value.StartsWith("CUST-", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleViolationException(
                "Customer number must start with 'CUST-'.");
        }

        var sequencePart = value["CUST-".Length..];

        if (!long.TryParse(sequencePart, out var sequenceNumber) ||
            sequenceNumber <= 0)
        {
            throw new DomainRuleViolationException(
                "Customer number must contain a valid sequence number.");
        }

        var normalizedValue = $"CUST-{sequenceNumber:D6}";

        if (normalizedValue.Length > 30)
        {
            throw new DomainRuleViolationException(
                "Customer number cannot exceed 30 characters.");
        }

        return new CustomerNumber(normalizedValue);
    }

    public override string ToString() => Value;
}