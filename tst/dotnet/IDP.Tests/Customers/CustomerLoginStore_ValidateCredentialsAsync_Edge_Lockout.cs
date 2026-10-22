using AwesomeAssertions;

using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Tests.Support;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Customers;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The customers' sign-in faces the internet: five wrong passwords lock it for 15
// │ minutes (counted in ONE UPDATE), a locked sign-in refuses even the right password, and
// │ the password rule (length, not the customer number) holds before anything is stored.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class CustomerLoginStore_ValidateCredentialsAsync_Edge_Lockout
{
    private const string Password = "a long and private phrase";

    private CustomerTestDatabase _database = null!;

    [SetUp]
    public void SetUp() => _database = new CustomerTestDatabase();

    [TearDown]
    public void TearDown() => _database.Dispose();

    [Test]
    [Category("Happy")]
    public async Task Given_ARegisteredCustomer_When_TheRightPasswordIsGiven_Then_TheyAreSignedIn()
    {
        // Given
        var camilla = await _database.RegisterCamillaAsync(Password);

        // When - typed in lower case, as people do
        var signedIn = await _database.Logins().ValidateCredentialsAsync("cust-100001", Password, CancellationToken.None);

        // Then
        signedIn.Should().NotBeNull();
        signedIn!.SubjectId.Should().Be(camilla.SubjectId);
    }

    [Test]
    [Category("Security")]
    public async Task Given_FiveWrongPasswords_When_TheRightOneIsGiven_Then_TheSignInIsLocked()
    {
        // Given
        var camilla = await _database.RegisterCamillaAsync(Password);
        for (var attempt = 1; attempt <= 5; attempt++)
            (await _database.Logins().ValidateCredentialsAsync("CUST-100001", "wrong password " + attempt, CancellationToken.None)).Should().BeNull();

        // When
        var signedIn = await _database.Logins().ValidateCredentialsAsync("CUST-100001", Password, CancellationToken.None);

        // Then
        signedIn.Should().BeNull("locked for 15 minutes");
        (await _database.Logins().IsLockedAsync(camilla.SubjectId, CancellationToken.None)).Should().BeTrue();
    }

    [Test]
    [Category("Security")]
    public async Task Given_ALockedSignIn_When_FifteenMinutesHavePassed_Then_TheRightPasswordWorksAgain()
    {
        // Given
        await _database.RegisterCamillaAsync(Password);
        for (var attempt = 1; attempt <= 5; attempt++)
            await _database.Logins().ValidateCredentialsAsync("CUST-100001", "wrong password " + attempt, CancellationToken.None);
        _database.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        // When
        var signedIn = await _database.Logins().ValidateCredentialsAsync("CUST-100001", Password, CancellationToken.None);

        // Then
        signedIn.Should().NotBeNull();
    }

    [Test]
    [Category("Security")]
    public async Task Given_AWrongSecondFactorCode_When_ItIsCounted_Then_ItCountsLikeAWrongPassword()
    {
        // Given
        var camilla = await _database.RegisterCamillaAsync(Password);

        // When
        await _database.Logins().CountFailureAsync(camilla.SubjectId, CancellationToken.None);

        // Then
        _database.Stored(camilla.SubjectId).AccessFailedCount.Should().Be(1);
    }

    [Test]
    [Category("Security")]
    public async Task Given_ACustomerWithASignIn_When_TheyRegisterAgain_Then_TheSecondIsRefused()
    {
        // Given
        await _database.RegisterCamillaAsync(Password);
        var second = new Data.Entities.CustomerLogin { SubjectId = Guid.NewGuid(), CustomerNumber = "CUST-100001", DisplayName = "Camilla", MobileEnding = "678" };

        // When
        var created = await _database.Logins().CreateAsync(second, Password, CancellationToken.None);

        // Then
        created.Should().BeFalse("one sign-in per customer number");
    }

    [TestCase("short", "short", TestName = "Given_AShortPassword_When_ItIsChecked_Then_ItIsRefused")]
    [TestCase("my number CUST-100001 x", "my number CUST-100001 x", TestName = "Given_APasswordWithTheCustomerNumber_When_ItIsChecked_Then_ItIsRefused")]
    [TestCase("a long and private phrase", "a long and private phrasE", TestName = "Given_TwoDifferentPasswords_When_TheyAreChecked_Then_TheyAreRefused")]
    [Category("Security")]
    public void PasswordRule(string password, string confirmation)
    {
        // When
        var problem = CustomerLoginStore.PasswordProblem("CUST-100001", password, confirmation);

        // Then
        problem.Should().NotBeNull();
    }

    [Test]
    [Category("Happy")]
    public void Given_ALongPrivatePassword_When_ItIsChecked_Then_ItIsAccepted() =>
        CustomerLoginStore.PasswordProblem("CUST-100001", Password, Password).Should().BeNull();
}