using System.Security.Cryptography;

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Privacy;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Accounts' own copy (the name an application is to be opened in) through its REAL unit of
// │ work, on an in-memory SQLite database so the raw column can be read: never in clear; the
// │ erasure deletes the customer's key in the same transaction, the application reads
// │ "Personal data erased", nobody acts on it and no account is ever opened from it.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class AccountsPersonalData_Erase_Edge_KeyDestroyed
{
    private readonly PersonalDataCipher _cipher = new(new PersonalDataProtectionOptions
    {
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    });

    private SqliteConnection _connection = null!;
    private AccountApplication _application = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var db = Context();
        db.Database.EnsureCreated();

        _application = AccountsTestData.PendingApplication();
        Repository(db).Add(_application);
        await db.SaveChangesAsync(new WorkflowContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private AccountsDbContext Context() =>
        new(new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite(_connection).Options, TimeProvider.System, _cipher);

    private AccountApplicationRepository Repository(AccountsDbContext db) => new(db, new AccountApplicationPersonalData(db, _cipher));

    private async Task<ErasePersonalDataResult> EraseAsync()
    {
        await using var db = Context();
        return await new ErasePersonalDataCommandHandler(Repository(db), db, TimeProvider.System, NullLogger<ErasePersonalDataCommandHandler>.Instance)
            .HandleAsync(new ErasePersonalDataCommand(AccountsTestData.Customer, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    }

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ANewApplication_When_ItIsSaved_Then_TheHolderIsStoredOnlyEncryptedAndReadsBack()
    {
        // When
        await using var db = Context();
        var loaded = await Repository(db).GetByApplicationRefAsync(_application.ApplicationRef, CancellationToken.None);

        // Then
        ((string)Scalar("SELECT holder_name FROM account_applications")!).Should().StartWith("v1.").And.NotContainAny("Ava", "Nguyen");
        loaded!.HolderName!.FullName.Should().Be("Ava Nguyen");
    }

    [Test]
    public async Task Given_AnApplication_When_TheCustomerIsErased_Then_TheKeyGoesAndTheApplicationReadsErased()
    {
        // When
        var result = await EraseAsync();

        // Then
        result.Should().Be(new ErasePersonalDataResult(AccountsTestData.Customer, RecordsErased: 1, RecordsHeld: 1));
        Convert.ToInt64(Scalar("SELECT count(*) FROM personal_data_keys")).Should().Be(0);

        await using var db = Context();
        var shown = await new AccountsQueries(db, new AccountApplicationPersonalData(db, _cipher))
            .GetApplicationAsync(_application.Id, AccountsTestData.Sydney, CancellationToken.None);
        shown!.CustomerName.Should().Be(AccountApplicationPersonalData.ErasedName);
        shown.PersonalDataErased.Should().BeTrue();

        // A redelivered erasure event erases nothing new.
        (await EraseAsync()).Should().Be(new ErasePersonalDataResult(AccountsTestData.Customer, RecordsErased: 0, RecordsHeld: 1));
    }

    [Test]
    public void Given_AnErasedApplication_When_AnOfficerApprovesIt_Then_ItIsRefusedAndNoAccountCanBeOpened()
    {
        // Given
        var application = AccountsTestData.PendingApplication();
        application.ErasePersonalData(AccountsTestData.Now);

        // When
        var approve = () => application.Approve(AccountsTestData.Jack, AccountProduct.EverydayTransaction, null, Guid.NewGuid(), AccountsTestData.Now);

        // Then
        approve.Should().Throw<DomainConflictException>().WithMessage("*personal data was erased*");
        application.HolderName.Should().BeNull();
        application.Status.Should().Be(AccountApplicationStatus.PendingReview);
    }
}