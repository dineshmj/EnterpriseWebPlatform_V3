using AwesomeAssertions;

using EnterpriseWebPlatform.IdentityServer.Tests.Support;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Security;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ AI review 1, fix R2: an intercepted or shoulder-surfed code must not work a second time.
// │ MfaService reads the authenticator WITHOUT tracking and never compares the last used
// │ step in C#: the only guard is the conditional UPDATE (... WHERE last_used_time_step <
// │ step). So a replay refused here is refused by that UPDATE - the same single statement
// │ that lets only one of two simultaneous requests win. A wrong code counts towards the
// │ lockout (5 in a row: 15 minutes), a right one resets the count.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class MfaService_VerifyAsync_Edge_OneTimeCodes
{
    private MfaTestDatabase _database = null!;

    [SetUp]
    public void SetUp() => _database = new MfaTestDatabase();

    [TearDown]
    public void TearDown() => _database.Dispose();

    [Test]
    [Category("Happy")]
    public async Task Given_AnEnrolledUser_When_TheNextCodeIsEntered_Then_ItIsAccepted()
    {
        // Given
        await _database.EnrolAsync();
        _database.Clock.Advance(TimeSpan.FromSeconds(30));

        // When
        var accepted = await _database.Service().VerifyAsync(_database.Daniel(), _database.CodeNow(), CancellationToken.None);

        // Then
        accepted.Should().BeTrue();
        _database.Daniel().AccessFailedCount.Should().Be(0);
    }

    [Test]
    [Category("Security")]
    public async Task Given_ACodeJustAccepted_When_TheSameCodeIsEnteredAgain_Then_ItIsRefused()
    {
        // Given
        await _database.EnrolAsync();
        _database.Clock.Advance(TimeSpan.FromSeconds(30));
        var code = _database.CodeNow();
        (await _database.Service().VerifyAsync(_database.Daniel(), code, CancellationToken.None)).Should().BeTrue();

        // When
        var replayed = await _database.Service().VerifyAsync(_database.Daniel(), code, CancellationToken.None);

        // Then
        replayed.Should().BeFalse("a code is claimed once: the UPDATE finds no row with an older step");
        _database.Daniel().AccessFailedCount.Should().Be(1);
    }

    [Test]
    [Category("Security")]
    public async Task Given_ACodeJustAccepted_When_TheCodeBeforeItIsEntered_Then_ItIsRefused()
    {
        // Given - the previous step's code is still inside the ±30 s drift window
        await _database.EnrolAsync();
        _database.Clock.Advance(TimeSpan.FromSeconds(60));
        (await _database.Service().VerifyAsync(_database.Daniel(), _database.CodeNow(), CancellationToken.None)).Should().BeTrue();

        // When
        var older = await _database.Service().VerifyAsync(_database.Daniel(), _database.CodeNow(-1), CancellationToken.None);

        // Then
        older.Should().BeFalse("the time step only moves forward");
    }

    [Test]
    [Category("Security")]
    public async Task Given_TheEnrolmentCode_When_ItIsUsedToSignIn_Then_ItIsRefused()
    {
        // Given
        await _database.EnrolAsync();

        // When
        var accepted = await _database.Service().VerifyAsync(_database.Daniel(), _database.CodeNow(), CancellationToken.None);

        // Then
        accepted.Should().BeFalse("enrolment already used this step");
    }

    [Test]
    [Category("Security")]
    public async Task Given_ARecoveryCode_When_ItIsUsedTwice_Then_OnlyTheFirstUseCounts()
    {
        // Given
        var recoveryCodes = await _database.EnrolAsync();
        var shown = IdentityServer.Security.MfaService.Display(recoveryCodes[0]);

        // When
        var firstUse = await _database.Service().VerifyAsync(_database.Daniel(), shown.ToLowerInvariant(), CancellationToken.None);
        var secondUse = await _database.Service().VerifyAsync(_database.Daniel(), shown, CancellationToken.None);

        // Then
        firstUse.Should().BeTrue("typed as shown (with the hyphen), any case");
        secondUse.Should().BeFalse();
    }

    [Test]
    [Category("Security")]
    public async Task Given_FourWrongCodes_When_TheFifthIsWrong_Then_TheAccountIsLockedFor15Minutes()
    {
        // Given
        await _database.EnrolAsync();
        for (var i = 0; i < 4; i++)
            await _database.Service().VerifyAsync(_database.Daniel(), "000000", CancellationToken.None);
        _database.Daniel().AccessFailedCount.Should().Be(4);

        // When
        await _database.Service().VerifyAsync(_database.Daniel(), "000000", CancellationToken.None);

        // Then
        var daniel = _database.Daniel();
        daniel.LockoutEnd.Should().Be(_database.Clock.GetUtcNow().AddMinutes(15));
        daniel.AccessFailedCount.Should().Be(0, "the count starts again after the lockout");
    }

    [Test]
    [Category("Security")]
    public async Task Given_ALockedAccount_When_TheRightCodeIsEntered_Then_ItIsRefusedUntilTheLockoutEnds()
    {
        // Given
        await _database.EnrolAsync();
        for (var i = 0; i < 5; i++)
            await _database.Service().VerifyAsync(_database.Daniel(), "000000", CancellationToken.None);
        _database.Clock.Advance(TimeSpan.FromSeconds(30));

        // When
        var whileLocked = await _database.Service().VerifyAsync(_database.Daniel(), _database.CodeNow(), CancellationToken.None);
        _database.Clock.Advance(TimeSpan.FromMinutes(15));
        var afterLockout = await _database.Service().VerifyAsync(_database.Daniel(), _database.CodeNow(), CancellationToken.None);

        // Then
        whileLocked.Should().BeFalse();
        afterLockout.Should().BeTrue();
    }

    [Test]
    public async Task Given_ThreeWrongCodes_When_TheRightCodeIsEntered_Then_TheCountIsReset()
    {
        // Given
        await _database.EnrolAsync();
        for (var i = 0; i < 3; i++)
            await _database.Service().VerifyAsync(_database.Daniel(), "000000", CancellationToken.None);
        _database.Clock.Advance(TimeSpan.FromSeconds(30));

        // When
        var accepted = await _database.Service().VerifyAsync(_database.Daniel(), _database.CodeNow(), CancellationToken.None);

        // Then
        accepted.Should().BeTrue();
        _database.Daniel().AccessFailedCount.Should().Be(0);
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnEnrolment_When_ItIsStored_Then_NeitherTheSecretNorARecoveryCodeIsReadable()
    {
        // When
        var recoveryCodes = await _database.EnrolAsync();

        // Then
        using var db = _database.NewContext();
        var stored = db.UserMfa.Single();
        stored.SecretProtected.Should().NotContain(Convert.ToBase64String(_database.Secret));
        db.UserMfaRecoveryCodes.Select(c => c.CodeHash).ToList()
            .Should().HaveCount(10).And.NotContain(recoveryCodes).And.OnlyContain(hash => hash.Length == 64);
    }
}