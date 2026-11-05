using System.Security.Cryptography;
using System.Text;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Privacy;

/// <summary>The master key of this context's personal-data protection (configuration "PersonalData").</summary>
public sealed class PersonalDataProtectionOptions
{
    public const string SectionName = "PersonalData";

    /// <summary>Base64 of 32 random bytes: wraps (encrypts) every customer's own data key.</summary>
    public string MasterKey { get; init; } = string.Empty;

    /// <summary>True when the key is base64 of exactly 32 bytes.</summary>
    public bool IsValid()
    {
        Span<byte> buffer = stackalloc byte[48];
        return Convert.TryFromBase64String(MasterKey, buffer, out var length) && length == 32;
    }
}

// ┌─ :: SECURITY :: Crypto-shredding of KYC's own copy - one key per customer ─────────────────────
// │ Why : KYC keeps the applicant as submitted (name, residential address). When Customer
// │       Onboarding erases a customer, this copy - and every backup of it - must become
// │       unreadable as well, without touching the case's outcome and people.
// │ How : The same scheme as Customer Onboarding, with KYC's OWN keys (squad autonomy: no shared
// │       key, no shared code): a random 256-bit data key per customer, stored only wrapped
// │       (AES-256-GCM under this context's master key); the applicant is AES-256-GCM under it,
// │       bound to the customer number. Deleting the wrapped key is the erasure.
// │       Format "v1.<base64 nonce | ciphertext | tag>".
// │ Ref : doc/Crypto-Shredding.md | GDPR Art. 17 | OWASP A02 Cryptographic Failures | NIST SP 800-38D
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class PersonalDataCipher
{
    private const string Version = "v1.";
    private const int KeySize = 32;
    private const int NonceSize = 12;   // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;     // AesGcm.TagByteSizes.MaxSize

    // The wrapped data key is bound to this label, so it can never be mistaken for a field value.
    private static readonly byte[] KeyWrapLabel = Encoding.UTF8.GetBytes("ewp/customer-kyc/data-key");

    private readonly byte[] _masterKey;

    public PersonalDataCipher(PersonalDataProtectionOptions options)
    {
        if (!options.IsValid())
            throw new ArgumentException("PersonalData:MasterKey must be base64 of 32 random bytes.", nameof(options));

        _masterKey = Convert.FromBase64String(options.MasterKey);
    }

    /// <summary>A new random data key for one customer, already wrapped by the master key (store this form only).</summary>
    public byte[] NewWrappedDataKey()
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            return Seal(_masterKey, dataKey, KeyWrapLabel);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Encrypts one value under the customer's wrapped data key.</summary>
    public string Encrypt(byte[] wrappedDataKey, string customerNumber, string plaintext)
    {
        var dataKey = Unwrap(wrappedDataKey);
        try
        {
            var sealedValue = Seal(dataKey, Encoding.UTF8.GetBytes(plaintext), AssociatedData(customerNumber));
            return Version + Convert.ToBase64String(sealedValue);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// Decrypts one value. Throws <see cref="CryptographicException"/> when it was altered,
    /// belongs to another customer or was sealed under another key.
    /// </summary>
    public string Decrypt(byte[] wrappedDataKey, string customerNumber, string protectedValue)
    {
        if (!protectedValue.StartsWith(Version, StringComparison.Ordinal))
            throw new CryptographicException("The value is not a protected personal field.");

        var dataKey = Unwrap(wrappedDataKey);
        try
        {
            var opened = Open(dataKey, Convert.FromBase64String(protectedValue[Version.Length..]), AssociatedData(customerNumber));
            return Encoding.UTF8.GetString(opened);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private byte[] Unwrap(byte[] wrappedDataKey) => Open(_masterKey, wrappedDataKey, KeyWrapLabel);

    private static byte[] AssociatedData(string customerNumber) =>
        Encoding.UTF8.GetBytes("ewp/customer/" + customerNumber);

    private static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        var ciphertext = output.AsSpan(NonceSize, plaintext.Length);
        var tag = output.AsSpan(NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    private static byte[] Open(byte[] key, byte[] sealedValue, byte[] associatedData)
    {
        if (sealedValue.Length < NonceSize + TagSize)
            throw new CryptographicException("The protected value is too short.");

        var nonce = sealedValue.AsSpan(0, NonceSize);
        var ciphertext = sealedValue.AsSpan(NonceSize, sealedValue.Length - NonceSize - TagSize);
        var tag = sealedValue.AsSpan(sealedValue.Length - TagSize, TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }
}