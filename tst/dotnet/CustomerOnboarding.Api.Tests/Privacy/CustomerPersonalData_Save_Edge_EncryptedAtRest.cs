using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Privacy;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Crypto-shredding through the REAL unit of work: what reaches the customers table is
// │ ciphertext under the customer's own key, the aggregate and the queries still see the
// │ plain values, exact-match search still works through the blind index - and once the
// │ key row is gone, nothing can read or rewrite that customer's personal data.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class CustomerPersonalData_Save_Edge_EncryptedAtRest
{
    private CustomerTestDatabase _database = null!;
    private long _avaId;

    [SetUp]
    public async Task SetUp()
    {
        _database = new CustomerTestDatabase();

        var ava = OnboardingTestData.Prospect();
        ava.AddAddress(
            CustomerAddress.Create(AddressType.Residential,
                PostalAddress.Create("1 George Street", "Level 4", "Sydney", "NSW", "2000", "AU"), isPrimary: true, OnboardingTestData.Now),
            OnboardingTestData.Now);

        await using var db = _database.Context();
        await CustomerTestDatabase.Repository(db, _database.Cipher).AddAsync(ava, CancellationToken.None);
        await db.SaveChangesAsync();
        _avaId = ava.Id;
    }

    [TearDown]
    public void TearDown() => _database.Dispose();

    [Test]
    public void Given_ASavedCustomer_When_TheRowIsReadDirectly_Then_EveryPersonalFieldIsCiphertext()
    {
        // When
        var row = new[]
        {
            _database.Raw("SELECT first_name FROM customers"),
            _database.Raw("SELECT last_name FROM customers"),
            _database.Raw("SELECT email FROM customers"),
            _database.Raw("SELECT phone_number FROM customers"),
            _database.Raw("SELECT address_line1 FROM customer_addresses"),
            _database.Raw("SELECT address_line2 FROM customer_addresses")
        };

        // Then
        row.Should().AllSatisfy(value => value.Should().StartWith("v1."));
        string.Concat(row).Should().NotContainAny("Ava", "Nguyen", "ava.nguyen", "412", "George", "Level");
        _database.Raw("SELECT city FROM customer_addresses").Should().Be("Sydney");
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ASavedCustomer_When_LoadedThroughTheRepository_Then_ThePlainValuesComeBack()
    {
        // Given
        await using var db = _database.Context();

        // When
        var ava = await CustomerTestDatabase.Repository(db, _database.Cipher).GetByIdAsync(_avaId, CancellationToken.None);

        // Then
        ava!.Name.Should().Be(PersonName.Create("Ava", "Nguyen"));
        ava.Email.Should().Be(EmailAddress.Create("ava.nguyen@example.com"));
        ava.PhoneNumber.Should().Be(PhoneNumber.Create("+61 412 345 678"));
        ava.Addresses.Single().Address.Should().Be(PostalAddress.Create("1 George Street", "Level 4", "Sydney", "NSW", "2000", "AU"));
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ASavedCustomer_When_ShownByTheQuery_Then_TheDetailsAreDecrypted()
    {
        // Given
        await using var db = _database.Context();
        var handler = new GetCustomerQueryHandler(db, new CustomerPersonalData(db, _database.Cipher));

        // When
        var details = await handler.HandleAsync(new GetCustomerQuery(_avaId), CancellationToken.None);

        // Then
        details!.FirstName.Should().Be("Ava");
        details.Email.Should().Be("ava.nguyen@example.com");
        details.ResidentialAddress!.AddressLine1.Should().Be("1 George Street");
    }

    [TestCase("ava.nguyen@example.com", true)]
    [TestCase("  AVA.Nguyen@Example.COM ", true)]
    [TestCase("nguyen", true)]
    [TestCase("ava.nguyen", false)]
    [TestCase("Nguy", false)]
    public async Task Given_ASavedCustomer_When_SearchedByEmailOrSurname_Then_OnlyAnExactMatchFindsThem(string search, bool found)
    {
        // Given - the blind index: exact (ignoring case and outer spaces), never "contains"
        await using var db = _database.Context();
        var personalData = new CustomerPersonalData(db, _database.Cipher);

        // When
        var matches = personalData.WhereEmailOrSurnameIs(db.Customers, search).Select(x => x.Id).ToList();

        // Then
        matches.Count.Should().Be(found ? 1 : 0);
    }

    [Test]
    public async Task Given_TheCustomersKeyWasDestroyed_When_TheyAreShownOrLoaded_Then_NothingPersonalCanBeRead()
    {
        // Given - erasure: the customer's wrapped data key is deleted, nothing else
        _database.Execute("DELETE FROM personal_data_keys");
        await using var db = _database.Context();

        // When
        var shown = (await new CustomerPersonalData(db, _database.Cipher).ReadAsync([_avaId], CancellationToken.None))[_avaId];
        var load = () => CustomerTestDatabase.Repository(db, _database.Cipher).GetByIdAsync(_avaId, CancellationToken.None);

        // Then
        shown.Should().Be(CustomerPersonalDetails.ErasedDetails);
        await load.Should().ThrowAsync<DomainRuleViolationException>().WithMessage("*erased*");
        _database.Raw("SELECT email FROM customers").Should().StartWith("v1.");
    }

    [Test]
    public async Task Given_TheCustomersKeyWasDestroyed_When_TheirDetailsAreWrittenAgain_Then_ItIsRefused()
    {
        // Given - a customer loaded before the erasure, changed after it
        await using var db = _database.Context();
        var ava = await CustomerTestDatabase.Repository(db, _database.Cipher).GetByIdAsync(_avaId, CancellationToken.None);
        _database.Execute("DELETE FROM personal_data_keys");
        db.ChangeTracker.Entries<PersonalDataKey>().Single().State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        // When
        ava!.ChangeContactDetails(EmailAddress.Create("ava@example.org"), PhoneNumber.Create("+61 400 000 000"), OnboardingTestData.Now);
        var save = () => db.SaveChangesAsync();

        // Then - no new key is made for an existing customer: their old ciphertext stays unreadable
        await save.Should().ThrowAsync<DomainRuleViolationException>().WithMessage("*erased*");
    }
}