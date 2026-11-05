using System.Security.Cryptography;

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.ErasePersonalData;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Privacy;
using EnterpriseWebPlatform.CustomerKyc.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ KYC's own copy through its REAL unit of work, on an in-memory SQLite database so the
// │ raw column can be read: the applicant is never written in clear, and the erasure deletes
// │ the customer's key in the same transaction - after which their cases read "Personal data
// │ erased" while every other customer's still read. Repeating it erases nothing new.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class KycPersonalData_Erase_Edge_KeyDestroyed
{
    private const string AvaNumber = "CUST-100001";
    private const string BenNumber = "CUST-100002";

    private readonly PersonalDataCipher _cipher = new(new PersonalDataProtectionOptions
    {
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    });

    private SqliteConnection _connection = null!;
    private KycCase _avaCase = null!;
    private KycCase _benCase = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var db = Context();
        db.Database.EnsureCreated();

        _avaCase = KycTestData.PendingCase();
        _benCase = KycCase.Open(
            Guid.NewGuid(), "APP-261028-TEST02", BenNumber, KycTestData.Sydney,
            Applicant.Create("Ben", "Okafor", "9 Pitt St", null, "Sydney", "NSW", "2000", "AU"),
            Guid.NewGuid(), Guid.NewGuid(), KycTestData.Sophie, KycTestData.Now);

        var repository = Repository(db);
        repository.Add(_avaCase);
        repository.Add(_benCase);
        await db.SaveChangesAsync(new WorkflowContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private KycDbContext Context() =>
        new(new DbContextOptionsBuilder<KycDbContext>().UseSqlite(_connection).Options, TimeProvider.System, _cipher);

    private KycCaseRepository Repository(KycDbContext db) => new(db, new KycCasePersonalData(db, _cipher));

    private async Task<ErasePersonalDataResult> EraseAsync(string customerNumber)
    {
        await using var db = Context();
        var handler = new ErasePersonalDataCommandHandler(
            Repository(db), db, TimeProvider.System, NullLogger<ErasePersonalDataCommandHandler>.Instance);
        return await handler.HandleAsync(
            new ErasePersonalDataCommand(customerNumber, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    }

    private async Task<KycCaseDetail> ShownAsync(KycCase kycCase)
    {
        await using var db = Context();
        return (await new KycCaseQueries(db, new KycCasePersonalData(db, _cipher))
            .GetCaseAsync(kycCase.Id, KycTestData.Sydney, CancellationToken.None))!;
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string RawApplicant(string customerNumber)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT applicant FROM kyc_cases WHERE customer_number = '{customerNumber}'";
        return (string)command.ExecuteScalar()!;
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ANewCase_When_ItIsSaved_Then_TheApplicantIsStoredOnlyEncryptedAndReadsBack()
    {
        // When - the case saved in SetUp, loaded as a decision would load it
        await using var db = Context();
        var loaded = await Repository(db).GetByApplicationRefAsync(_avaCase.ApplicationRef, CancellationToken.None);

        // Then
        RawApplicant(AvaNumber).Should().StartWith("v1.").And.NotContainAny("Ava", "Nguyen", "George");
        Scalar($"SELECT count(*) FROM personal_data_keys WHERE customer_number = '{AvaNumber}'").Should().Be(1);
        loaded!.Applicant!.FullName.Should().Be("Ava Nguyen");
        loaded.Applicant.City.Should().Be("Sydney");
    }

    [Test]
    public async Task Given_TwoCustomers_When_AvaIsErased_Then_HerKeyGoesAndOnlyHerCaseReadsErased()
    {
        // When
        var result = await EraseAsync(AvaNumber);

        // Then - her key is gone, the ciphertext is still there, unreadable
        result.Should().Be(new ErasePersonalDataResult(AvaNumber, RecordsErased: 1, RecordsHeld: 1));
        Scalar($"SELECT count(*) FROM personal_data_keys WHERE customer_number = '{AvaNumber}'").Should().Be(0);
        RawApplicant(AvaNumber).Should().StartWith("v1.");

        var ava = await ShownAsync(_avaCase);
        ava.CustomerName.Should().Be(KycCasePersonalData.ErasedName);
        ava.ResidentialAddress.Should().Be(new ApplicantAddress(null, null, null, null, null, null));
        ava.PersonalDataErased.Should().BeTrue();
        ava.ApplicationNumber.Should().Be("APP-261028-TEST01");

        // Then - Ben is untouched
        Scalar($"SELECT count(*) FROM personal_data_keys WHERE customer_number = '{BenNumber}'").Should().Be(1);
        var ben = await ShownAsync(_benCase);
        ben.CustomerName.Should().Be("Ben Okafor");
        ben.PersonalDataErased.Should().BeFalse();
    }

    [Test]
    public async Task Given_AnErasedCustomer_When_TheErasureArrivesAgain_Then_ItErasesNothingNewAndAnswersTheSame()
    {
        // Given
        await EraseAsync(AvaNumber);

        // When - a redelivered erasure event
        var again = await EraseAsync(AvaNumber);

        // Then
        again.Should().Be(new ErasePersonalDataResult(AvaNumber, RecordsErased: 0, RecordsHeld: 1));
    }

    [Test]
    public async Task Given_ACustomerKycNeverSaw_When_TheErasureArrives_Then_ItHoldsNothingAndSaysSo()
    {
        // When
        var result = await EraseAsync("CUST-999999");

        // Then - still answered, so the subscriber can confirm "KYC holds no copy"
        result.Should().Be(new ErasePersonalDataResult("CUST-999999", RecordsErased: 0, RecordsHeld: 0));
        Scalar("SELECT count(*) FROM personal_data_keys").Should().Be(2);
    }
}