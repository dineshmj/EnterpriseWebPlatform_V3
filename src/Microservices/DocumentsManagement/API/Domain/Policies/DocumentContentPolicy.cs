using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Policies;

// ┌─ :: SECURITY :: Content type from the bytes, not the claim ────────────────────────────────────
// │ Why : An uploader can call any file "application/pdf"; trusting that lets a script be served as
// │       a document.
// │ How : The type is detected from the file's signature (magic bytes) against an allow-list (PDF,
// │       PNG, JPEG); a mismatch with the declared type is refused; the verified type is stored and
// │       served.
// │ Ref : Features 2.4.4 | OWASP File Upload Cheat Sheet | CWE-434 | AI review 1 (stored XSS)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Allow-list of document content types, verified against the file signature
/// (magic bytes). The caller's declared Content-Type is never trusted on its own:
/// the stored and served type is always the type DM detected.
/// </summary>
public static class DocumentContentPolicy
{
    public const int SignatureLength = 8;

    private static readonly (string ContentType, byte[] Signature)[] AllowedSignatures =
    [
        ("application/pdf", "%PDF-"u8.ToArray()),
        ("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        ("image/jpeg", [0xFF, 0xD8, 0xFF])
    ];

    public static string VerifyContentType(
        ReadOnlySpan<byte> header,
        string? declaredContentType)
    {
        string? detected = null;

        foreach (var (contentType, signature) in AllowedSignatures)
        {
            if (header.StartsWith(signature))
            {
                detected = contentType;
                break;
            }
        }

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the bytes match no allowed signature, refuse the upload - whatever the file name
        // │ or declared type say.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (detected is null)
        {
            throw new DomainRuleViolationException(
                "The document type is not supported. Allowed types: PDF, PNG, JPEG.");
        }

        var declared = declaredContentType?.Split(';')[0].Trim();

        if (!string.IsNullOrEmpty(declared) &&
            !string.Equals(declared, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(declared, detected, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleViolationException(
                "The declared content type does not match the document content.");
        }

        return detected;
    }
}