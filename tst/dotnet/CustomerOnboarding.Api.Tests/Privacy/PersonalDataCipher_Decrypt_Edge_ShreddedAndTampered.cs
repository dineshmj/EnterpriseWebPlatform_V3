using System.Security.Cryptography;

using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Crypto-shredding holds only if a personal field opens with exactly ONE thing: the
// │ customer's own data key, unwrapped by the master key, for that customer's number. A
// │ new key for the same customer, another customer's row, a changed byte or another
// │ master key must all fail - loudly, never as garbled text.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PersonalDataCipher_Decrypt_Edge_ShreddedAndTampered
{
    private const string Ava = "CUS-000123";
    private const string Noah = "CUS-000456";

    private static readonly PersonalDataProtectionOptions Keys = NewKeys();

    private PersonalDataCipher _cipher = null!;

    [SetUp]
    public void SetUp() => _cipher = new PersonalDataCipher(Keys);

    [Test]
    [Category("Happy")]
    public void Given_AFieldSealedUnderTheCustomersKey_When_Decrypted_Then_TheValueComesBack()
    {
        // Given
        var key = _cipher.NewWrappedDataKey();
        var sealedName = _cipher.Encrypt(key, Ava, "Ava Nguyen");

        // When
        var name = _cipher.Decrypt(key, Ava, sealedName);

        // Then
        name.Should().Be("Ava Nguyen");
        sealedName.Should().StartWith("v1.").And.NotContain("Ava");
    }

    [Test]
    public void Given_TheSameValueSealedTwice_When_Compared_Then_TheyDiffer()
    {
        // Given - a fresh nonce each time, so equal values cannot be spotted in the table
        var key = _cipher.NewWrappedDataKey();

        // When
        var first = _cipher.Encrypt(key, Ava, "Ava Nguyen");
        var second = _cipher.Encrypt(key, Ava, "Ava Nguyen");

        // Then
        first.Should().NotBe(second);
    }

    [Test]
    public void Given_TheCustomersKeyWasDestroyed_When_TheOldValueIsDecryptedWithANewKey_Then_ItCannotBeRead()
    {
        // Given - erasure deleted the wrapped key; a key made later for the same customer is another key
        var shreddedKey = _cipher.NewWrappedDataKey();
        var sealedName = _cipher.Encrypt(shreddedKey, Ava, "Ava Nguyen");
        var laterKey = _cipher.NewWrappedDataKey();

        // When
        var read = () => _cipher.Decrypt(laterKey, Ava, sealedName);

        // Then
        read.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Given_AValueCopiedOntoAnotherCustomer_When_Decrypted_Then_ItFailsAuthentication()
    {
        // Given - the same key, but the customer number (associated data) differs
        var key = _cipher.NewWrappedDataKey();
        var sealedName = _cipher.Encrypt(key, Ava, "Ava Nguyen");

        // When
        var read = () => _cipher.Decrypt(key, Noah, sealedName);

        // Then
        read.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Given_OneByteOfTheValueChanged_When_Decrypted_Then_ItFailsAuthentication()
    {
        // Given
        var key = _cipher.NewWrappedDataKey();
        var sealedValue = Convert.FromBase64String(_cipher.Encrypt(key, Ava, "Ava Nguyen")["v1.".Length..]);
        sealedValue[^20] ^= 0x01;   // inside the ciphertext, just before the tag
        var tampered = "v1." + Convert.ToBase64String(sealedValue);

        // When
        var read = () => _cipher.Decrypt(key, Ava, tampered);

        // Then
        read.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Given_AnotherMasterKey_When_TheWrappedKeyIsUsed_Then_ItCannotBeUnwrapped()
    {
        // Given - e.g. a database restored into an environment with its own master key
        var key = _cipher.NewWrappedDataKey();
        var sealedName = _cipher.Encrypt(key, Ava, "Ava Nguyen");
        var elsewhere = new PersonalDataCipher(NewKeys());

        // When
        var read = () => elsewhere.Decrypt(key, Ava, sealedName);

        // Then
        read.Should().Throw<CryptographicException>();
    }

    [TestCase("Ava Nguyen")]
    [TestCase("")]
    [TestCase("v2.AAAA")]
    public void Given_AValueNotInTheProtectedFormat_When_Decrypted_Then_ItIsRefused(string value)
    {
        // Given
        var key = _cipher.NewWrappedDataKey();

        // When
        var read = () => _cipher.Decrypt(key, Ava, value);

        // Then
        read.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Given_AWrappedDataKey_When_UsedAsAFieldValue_Then_ItCannotBeOpenedAsOne()
    {
        // Given - the wrapped key is sealed under the master key with its own label
        var key = _cipher.NewWrappedDataKey();
        var keyAsField = "v1." + Convert.ToBase64String(key);

        // When
        var read = () => _cipher.Decrypt(key, Ava, keyAsField);

        // Then
        read.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Given_TheSameNormalisedValue_When_BlindIndexed_Then_TheIndexIsStableAndRevealsNothing()
    {
        // When
        var first = _cipher.BlindIndex("ava.nguyen@example.com");
        var again = _cipher.BlindIndex("ava.nguyen@example.com");
        var other = _cipher.BlindIndex("noah.tran@example.com");
        var underAnotherKey = new PersonalDataCipher(NewKeys()).BlindIndex("ava.nguyen@example.com");

        // Then
        first.Should().Be(again).And.MatchRegex("^[0-9a-f]{64}$");
        other.Should().NotBe(first);
        underAnotherKey.Should().NotBe(first);
    }

    [Test]
    public void Given_KeysThatAreMissingShortOrTheSame_When_TheCipherIsMade_Then_ItIsRefused()
    {
        // Given
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var oneKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // When
        var missing = () => new PersonalDataCipher(new PersonalDataProtectionOptions());
        var tooShort = () => new PersonalDataCipher(new PersonalDataProtectionOptions { MasterKey = shortKey, BlindIndexKey = oneKey });
        var same = () => new PersonalDataCipher(new PersonalDataProtectionOptions { MasterKey = oneKey, BlindIndexKey = oneKey });

        // Then
        missing.Should().Throw<ArgumentException>();
        tooShort.Should().Throw<ArgumentException>();
        same.Should().Throw<ArgumentException>();
    }

    private static PersonalDataProtectionOptions NewKeys() => new()
    {
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    };
}