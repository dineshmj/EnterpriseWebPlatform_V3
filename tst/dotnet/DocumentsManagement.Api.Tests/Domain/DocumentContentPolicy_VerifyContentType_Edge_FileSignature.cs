using System.Text;

using AwesomeAssertions;

using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Policies;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The stored content type comes from the file's own bytes (PDF, PNG, JPEG only), never
// │ from what the uploader claims: a script called "application/pdf" is refused, and so
// │ is a real PDF declared as something else. File names lose any path and unsafe chars.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class DocumentContentPolicy_VerifyContentType_Edge_FileSignature
{
    private static readonly byte[] Pdf = "%PDF-1.7\n"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    private static IEnumerable<TestCaseData> AllowedFiles()
    {
        yield return new TestCaseData(Pdf, "application/pdf", "application/pdf");
        yield return new TestCaseData(Pdf, "application/pdf; charset=binary", "application/pdf");
        yield return new TestCaseData(Png, "IMAGE/PNG", "image/png");
        yield return new TestCaseData(Jpeg, null, "image/jpeg");
        yield return new TestCaseData(Jpeg, "application/octet-stream", "image/jpeg");
    }

    [TestCaseSource(nameof(AllowedFiles))]
    [Category("Happy")]
    public void Given_AnAllowedFile_When_ItsTypeIsVerified_Then_TheTypeDetectedFromTheBytesIsKept(
        byte[] header, string? declared, string expected)
    {
        // Given / When / Then
        DocumentContentPolicy.VerifyContentType(header, declared).Should().Be(expected);
    }

    [TestCase("<script>alert(1)</script>", "application/pdf")]
    [TestCase("<html><body>", "text/html")]
    [TestCase("MZ\u0090\u0000", "application/octet-stream")]
    [TestCase("", "application/pdf")]
    public void Given_ContentMatchingNoAllowedSignature_When_ItsTypeIsVerified_Then_ItIsRefused(string content, string declared)
    {
        // Given
        var header = Encoding.Latin1.GetBytes(content);

        // When
        var verify = () => DocumentContentPolicy.VerifyContentType(header, declared);

        // Then
        verify.Should().Throw<DomainRuleViolationException>().WithMessage("*not supported*");
    }

    [Test]
    public void Given_ARealPdfDeclaredAsAnImage_When_ItsTypeIsVerified_Then_ItIsRefused()
    {
        // Given / When
        var verify = () => DocumentContentPolicy.VerifyContentType(Pdf, "image/png");

        // Then
        verify.Should().Throw<DomainRuleViolationException>().WithMessage("*does not match*");
    }

    [TestCase("../../etc/passport.pdf", "passport.pdf")]
    [TestCase("pass<port>?.pdf", "passport.pdf")]
    [TestCase("  scan\u0000.png  ", "scan.png")]
    [Category("Boundary")]
    public void Given_AnUnsafeFileName_When_ItIsRecorded_Then_PathAndUnsafeCharactersAreRemoved(string input, string expected)
    {
        // Given / When / Then
        FileName.Create(input).Value.Should().Be(expected);
    }

    [TestCase("")]
    [TestCase("folder/")]
    [TestCase("..")]
    [TestCase("???")]
    [Category("Boundary")]
    public void Given_NoUsableFileName_When_ItIsRecorded_Then_ItIsRefused(string input)
    {
        // Given / When
        var create = () => FileName.Create(input);

        // Then
        create.Should().Throw<DomainRuleViolationException>();
    }
}