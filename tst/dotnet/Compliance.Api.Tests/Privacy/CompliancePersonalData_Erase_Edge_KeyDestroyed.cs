using System.Security.Cryptography;

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Application.Commands;
using EnterpriseWebPlatform.Compliance.Api.Application.Queries;
using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Privacy;
using EnterpriseWebPlatform.Compliance.Api.Tests.Support;

namespace EnterpriseWebPlatform.Compliance.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Compliance's own copy through its REAL unit of work, on an in-memory SQLite database so
// │ the raw column can be read: the applicant is never written in clear; the erasure deletes
// │ the customer's key in the same transaction, the case reads "Personal data erased", it is
// │ never screened again and nobody can act on it. Repeating it erases nothing new.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class CompliancePersonalData_Erase_Edge_KeyDestroyed
{
    private const string AvaNumber = "CUST-100001";

    private readonly PersonalDataCipher _cipher = new(new PersonalDataProtectionOptions
    {
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    });

    private SqliteConnection _connection = null!;
    private ComplianceCase _avaCase = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var db = Context();
        db.Database.EnsureCreated();

        _avaCase = ComplianceTestData.UnderReviewCase();
        Repository(db).Add(_avaCase);
        await db.SaveChangesAsync(new WorkflowContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private ComplianceDbContext Context() =>
        new(new DbContextOptionsBuilder<ComplianceDbContext>().UseSqlite(_connection).Options, TimeProvider.System, _cipher);

    private ComplianceCaseRepository Repository(ComplianceDbContext db) => new(db, new ComplianceCasePersonalData(db, _cipher));

    private async Task<ErasePersonalDataResult> EraseAsync()
    {
        await using var db = Context();
        return await new ErasePersonalDataCommandHandler(Repository(db), db, TimeProvider.System, NullLogger<ErasePersonalDataCommandHandler>.Instance)
            .HandleAsync(new ErasePersonalDataCommand(AvaNumber, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    }

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ANewCase_When_ItIsSaved_Then_TheApplicantIsStoredOnlyEncryptedAndReadsBack()
    {
        // When
        await using var db = Context();
        var loaded = await Repository(db).GetByApplicationRefAsync(_avaCase.ApplicationRef, CancellationToken.None);

        // Then
        ((string)Scalar("SELECT applicant FROM compliance_cases")!).Should().StartWith("v1.").And.NotContainAny("Ava", "Nguyen", "George");
        loaded!.Applicant!.FullName.Should().Be("Ava Nguyen");
    }

    [Test]
    public async Task Given_ACase_When_TheCustomerIsErased_Then_TheKeyGoesAndTheCaseReadsErased()
    {
        // When
        var result = await EraseAsync();

        // Then
        result.Should().Be(new ErasePersonalDataResult(AvaNumber, RecordsErased: 1, RecordsHeld: 1));
        Convert.ToInt64(Scalar("SELECT count(*) FROM personal_data_keys")).Should().Be(0);
        ((string)Scalar("SELECT applicant FROM compliance_cases")!).Should().StartWith("v1.");

        await using var db = Context();
        var shown = await new ComplianceCaseQueries(db, new ComplianceCasePersonalData(db, _cipher))
            .GetCaseAsync(_avaCase.Id, ComplianceTestData.Sydney, CancellationToken.None);
        shown!.CustomerName.Should().Be(ComplianceCasePersonalData.ErasedName);
        shown.ResidentialAddress.Should().Be(new ApplicantAddress(null, null, null, null, null, null));
        shown.PersonalDataErased.Should().BeTrue();
        shown.RiskRating.Should().NotBeNull();
    }

    [Test]
    public async Task Given_AnErasedCustomer_When_TheErasureArrivesAgain_Then_ItErasesNothingNew()
    {
        // Given
        await EraseAsync();

        // When
        var again = await EraseAsync();

        // Then
        again.Should().Be(new ErasePersonalDataResult(AvaNumber, RecordsErased: 0, RecordsHeld: 1));
    }

    [Test]
    public void Given_AnErasedCase_When_AnOfficerActsOnIt_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();
        complianceCase.ErasePersonalData(ComplianceTestData.Now);

        // When
        var claim = () => complianceCase.Claim(ComplianceTestData.Olivia, ComplianceTestData.Now);
        var reject = () => complianceCase.Reject(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // Then
        claim.Should().Throw<DomainConflictException>().WithMessage("*personal data was erased*");
        reject.Should().Throw<DomainConflictException>();
        complianceCase.Status.Should().Be(ComplianceCaseStatus.UnderReview);
        complianceCase.Applicant.Should().BeNull();
    }
}