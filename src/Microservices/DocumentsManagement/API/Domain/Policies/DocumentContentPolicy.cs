using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Policies;

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
