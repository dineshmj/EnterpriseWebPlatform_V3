using AwesomeAssertions;

using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Tests.Support;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Customers;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A six-digit code is guessable unless it is short-lived, has few tries and works once.
// │ The tries are counted on the database row - never in the browser's cookie - so a
// │ replayed cookie earns no fresh tries; only a salted hash of the code is stored.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class CustomerOneTimeCodes_CheckAsync_Edge_OneUse
{
    private CustomerTestDatabase _database = null!;

    [SetUp]
    public void SetUp() => _database = new CustomerTestDatabase();

    [TearDown]
    public void TearDown() => _database.Dispose();

    [Test]
    [Category("Happy")]
    public async Task Given_AFreshCode_When_ItIsEntered_Then_ItIsAccepted()
    {
        // Given
        var (id, code) = await _database.IssueRegistrationCodeAsync();

        // When
        var check = await _database.Codes().CheckAsync((await _database.OpenAsync(id))!, code, CancellationToken.None);

        // Then
        check.Should().Be(CodeCheck.Accepted);
    }

    [Test]
    [Category("Security")]
    public async Task Given_ACodeJustAccepted_When_ItIsEnteredAgain_Then_ItIsSpent()
    {
        // Given - the page read the row once, before the first use (a second tab, a replayed form)
        var (id, code) = await _database.IssueRegistrationCodeAsync();
        var open = (await _database.OpenAsync(id))!;
        (await _database.Codes().CheckAsync(open, code, CancellationToken.None)).Should().Be(CodeCheck.Accepted);

        // When
        var again = await _database.Codes().CheckAsync(open, code, CancellationToken.None);

        // Then
        again.Should().Be(CodeCheck.Spent, "the row is claimed by one conditional UPDATE: the second finds it used");
        (await _database.OpenAsync(id)).Should().BeNull();
    }

    [Test]
    [Category("Security")]
    public async Task Given_FiveWrongCodes_When_TheRightCodeIsEntered_Then_ItIsSpent()
    {
        // Given
        var (id, code) = await _database.IssueRegistrationCodeAsync();
        var wrong = code == "000000" ? "111111" : "000000";
        for (var attempt = 1; attempt <= 4; attempt++)
            (await _database.Codes().CheckAsync((await _database.OpenAsync(id))!, wrong, CancellationToken.None)).Should().Be(CodeCheck.Wrong);
        var lastOpen = (await _database.OpenAsync(id))!;
        (await _database.Codes().CheckAsync(lastOpen, wrong, CancellationToken.None)).Should().Be(CodeCheck.Spent, "the fifth wrong code uses the last try");

        // When
        var right = await _database.Codes().CheckAsync(lastOpen, code, CancellationToken.None);

        // Then
        right.Should().Be(CodeCheck.Spent, "five tries, then not even the right code passes");
    }

    [Test]
    [Category("Security")]
    public async Task Given_ACodeOlderThanFiveMinutes_When_ItIsEntered_Then_ItIsSpent()
    {
        // Given
        var (id, code) = await _database.IssueRegistrationCodeAsync();
        var open = (await _database.OpenAsync(id))!;
        _database.Clock.Advance(CustomerOneTimeCodes.Lifetime + TimeSpan.FromSeconds(1));

        // When
        var check = await _database.Codes().CheckAsync(open, code, CancellationToken.None);

        // Then
        check.Should().Be(CodeCheck.Spent);
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnIssuedCode_When_TheRowIsRead_Then_OnlyASaltedHashIsStored()
    {
        // Given
        var (id, code) = await _database.IssueRegistrationCodeAsync();

        // When
        var row = (await _database.OpenAsync(id))!;

        // Then
        row.CodeHash.Should().HaveLength(64).And.NotContain(code);
        row.Salt.Should().HaveLength(32);
    }
}