namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Privacy;

/// <summary>One customer's data key, wrapped by this context's master key. Deleting the row is the erasure.</summary>
public sealed class PersonalDataKey
{
    private PersonalDataKey()
    {
        CustomerNumber = null!;
        WrappedKey = null!;
    }

    public PersonalDataKey(string customerNumber, byte[] wrappedKey, DateTimeOffset createdAt)
    {
        CustomerNumber = customerNumber;
        WrappedKey = wrappedKey;
        CreatedAt = createdAt;
    }

    public string CustomerNumber { get; private set; }

    public byte[] WrappedKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}