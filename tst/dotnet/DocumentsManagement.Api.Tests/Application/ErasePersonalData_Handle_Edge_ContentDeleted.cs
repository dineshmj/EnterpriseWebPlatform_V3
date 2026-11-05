using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.ErasePersonalData;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Api.Tests.Application;

// ┌─ SEC + RESIL ────────────────────────────────────────────────────────────────────────
// │ An erasure blanks every file name and deletes every scan filed under the customer -
// │ AFTER the rows are saved, and again on a retry, so a delete that failed half-way never
// │ leaves a scan behind once the subscriber has confirmed. The rows stay as the record.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class ErasePersonalData_Handle_Edge_ContentDeleted
{
    private const string Customer = "CUST-100001";

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private Mock<IDocumentRepository> _repository = null!;
    private Mock<IDocumentStorage> _storage = null!;
    private List<string> _calls = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IDocumentRepository>();
        _storage = new Mock<IDocumentStorage>();
        _calls = [];
    }

    private static Document Uploaded(string fileName) =>
        Document.Upload(
            Guid.NewGuid(), FileName.Create(fileName), "application/pdf", size: 2048,
            ContentHash.Create(new string('a', 64)), $"{Guid.NewGuid():N}/content.enc", BranchCode.Create("SYD001"), Now, "KYCProof", Customer);

    private ErasePersonalDataCommandHandler Handler(params Document[] documents)
    {
        _repository.Setup(x => x.ListForErasureAsync(Customer, It.IsAny<CancellationToken>())).ReturnsAsync(documents);
        _repository.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("save")).Returns(Task.CompletedTask);
        _storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((reference, _) => _calls.Add("delete " + reference)).Returns(Task.CompletedTask);

        return new ErasePersonalDataCommandHandler(
            _repository.Object, _storage.Object, TimeProvider.System, NullLogger<ErasePersonalDataCommandHandler>.Instance);
    }

    [Test]
    [Category("Happy")]
    public async Task Given_TwoScans_When_TheCustomerIsErased_Then_TheNamesGoAndBothContentsAreDeletedAfterTheSave()
    {
        // Given
        var passport = Uploaded("ava-nguyen-passport.pdf");
        var tax = Uploaded("ava-nguyen-tfn.pdf");

        // When
        var result = await Handler(passport, tax).HandleAsync(new ErasePersonalDataCommand(Customer, Guid.NewGuid()), CancellationToken.None);

        // Then
        result.Should().Be(new ErasePersonalDataResult(Customer, RecordsErased: 2, RecordsHeld: 2));
        passport.FileName.Value.Should().Be(Document.ErasedFileName);
        tax.IsPersonalDataErased.Should().BeTrue();
        passport.ContentHash.Value.Should().Be(new string('a', 64));
        _calls.Should().Equal("save", "delete " + passport.StorageReference, "delete " + tax.StorageReference);
    }

    [Test]
    public async Task Given_ScansErasedBefore_When_TheErasureArrivesAgain_Then_TheirContentIsDeletedAgain()
    {
        // Given - the first try saved the rows, then failed before deleting the files
        var passport = Uploaded("ava-nguyen-passport.pdf");
        passport.ErasePersonalData(Now);

        // When - the redelivered erasure event
        var result = await Handler(passport).HandleAsync(new ErasePersonalDataCommand(Customer, Guid.NewGuid()), CancellationToken.None);

        // Then
        result.Should().Be(new ErasePersonalDataResult(Customer, RecordsErased: 0, RecordsHeld: 1));
        _calls.Should().Contain("delete " + passport.StorageReference);
    }

    [Test]
    public void Given_AnErasedScan_When_AnApplicationAttachesIt_Then_ItStaysUnattached()
    {
        // Given
        var passport = Uploaded("ava-nguyen-passport.pdf");
        passport.ErasePersonalData(Now);

        // When
        var attached = passport.Attach("APP-20261105-000001", Now);

        // Then
        attached.Should().BeFalse();
        passport.AttachedTo.Should().BeNull();
    }
}