using System.Security.Cryptography;
using System.Text;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

/// <summary>The master keys of personal-data protection (configuration "PersonalData").</summary>
public sealed class PersonalDataProtectionOptions
{
    public const string SectionName = "PersonalData";

    /// <summary>Base64 of 32 random bytes: wraps (encrypts) every customer's own data key.</summary>
    public string MasterKey { get; init; } = string.Empty;

    /// <summary>Base64 of 32 random bytes: keys the blind index (exact-match lookups on encrypted values).</summary>
    public string BlindIndexKey { get; init; } = string.Empty;

    /// <summary>True when both keys are base64 of exactly 32 bytes, and not the same key.</summary>
    public bool IsValid() =>
        Is256BitKey(MasterKey) && Is256BitKey(BlindIndexKey) && MasterKey != BlindIndexKey;

    private static bool Is256BitKey(string value)
    {
        Span<byte> buffer = stackalloc byte[48];
        return Convert.TryFromBase64String(value, buffer, out var length) && length == 32;
    }
}

// ┌─ :: SECURITY :: Crypto-shredding - one key per customer, wrapped by a master key ──────────────
// │ Why : The bank must erase a person's personal data once retention ends, but copies live where
// │       nothing may be changed: the Kafka topics, the Outbox, backups, the append-only audit
// │       trail's evidence. Deleting every copy is not possible; making every copy unreadable is.
// │ How : Each customer gets a random 256-bit data key; only its WRAPPED form (AES-256-GCM under
// │       the master key) is stored. Personal fields are AES-256-GCM under the customer's data key,
// │       with the customer number as associated data: a value copied onto another customer's row
// │       fails authentication instead of decrypting. Destroying the customer's wrapped key makes
// │       every copy of every field unreadable at once - that is the erasure.
// │       Format "v1.<base64 nonce | ciphertext | tag>"; the version leaves room for key rotation.
// │ Ref : GDPR Art. 17 | APP 11.2 | OWASP A02 Cryptographic Failures | NIST SP 800-38D
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class PersonalDataCipher
{
    private const string Version = "v1.";
    private const int KeySize = 32;
    private const int NonceSize = 12;   // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;     // AesGcm.TagByteSizes.MaxSize

    // The wrapped data key is bound to this label, so it can never be mistaken for a field value.
    private static readonly byte[] KeyWrapLabel = Encoding.UTF8.GetBytes("ewp/customer-onboarding/data-key");

    private readonly byte[] _masterKey;
    private readonly byte[] _blindIndexKey;

    public PersonalDataCipher(PersonalDataProtectionOptions options)
    {
        if (!options.IsValid())
            throw new ArgumentException(
                "PersonalData:MasterKey and PersonalData:BlindIndexKey must each be base64 of 32 random bytes, and differ.",
                nameof(options));

        _masterKey = Convert.FromBase64String(options.MasterKey);
        _blindIndexKey = Convert.FromBase64String(options.BlindIndexKey);
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

    /// <summary>Encrypts one personal field under the customer's wrapped data key.</summary>
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
    /// Decrypts one personal field. Throws <see cref="CryptographicException"/> when the value was
    /// altered, belongs to another customer or was sealed under another key.
    /// </summary>
    public string Decrypt(byte[] wrappedDataKey, string customerNumber, string protectedValue)
    {
        if (!IsProtected(protectedValue))
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

    /// <summary>True when the value has the protected-field format (it says nothing about which key sealed it).</summary>
    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Version, StringComparison.Ordinal);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Blind index: HMAC-SHA256 under its own key, so the duplicate and exact-match lookups
    // │ still run in SQL without the plaintext. It is NOT erased with the customer's key: on
    // │ its own it only confirms a value someone already knows (a guessed e-mail), so erasure
    // │ must clear it as well, and it never indexes free text.
    // └──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The blind index of an already normalised value (e.g. a lower-cased e-mail), as 64 hex characters.</summary>
    public string BlindIndex(string normalisedValue) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_blindIndexKey, Encoding.UTF8.GetBytes(normalisedValue)));

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