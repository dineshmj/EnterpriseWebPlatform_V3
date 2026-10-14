using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;

namespace EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Storage;

// ┌─ :: SECURITY :: Envelope encryption at rest ───────────────────────────────────────────────────
// │ Why : KYC documents are the most sensitive data in the platform; a copied disk or backup must
// │       reveal nothing.
// │ How : Each document gets its own AES-256-GCM key, wrapped by a rotatable key-encryption key;
// │       the document ID is bound in as associated data, so a swapped file fails; no key ring = no
// │       start; plaintext files are refused.
// │ Ref : Features 2.4.4 | Threat-Model R-2 | OWASP A02 | NIST SP 800-38D (GCM) | AI review 1 #2
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Encryption at rest for customer evidence (driver licences, tax notices): every file is
/// written encrypted, so a copy of the disk, a backup or a container escape yields no readable
/// document. Envelope encryption:
///  - each document gets its own random 256-bit data key; the content is sealed with AES-256-GCM
///    (confidentiality AND integrity: a changed byte makes the file unreadable, never wrong);
///  - the data key is itself sealed ("wrapped") with the current key-encryption key from
///    <see cref="DocumentKeyRing"/> and kept in the file's header with that key's ID - so a key
///    can be rotated without touching old files (they name the key that opens them);
///  - the document ID is bound into both seals: a file copied over another document's file
///    does not open.
/// The size and SHA-256 recorded for a document are those of the PLAINTEXT, as before.
///
/// File layout (all big-endian lengths):
///   "EWPDOC1\0" | key ID length (1) | key ID (ASCII) | wrap nonce (12) | wrapped data key (32) |
///   wrap tag (16) | content nonce (12) | content tag (16) | ciphertext
/// </summary>
public sealed class EncryptedDocumentStorage(IDocumentStorage inner, DocumentKeyRing keys) : IDocumentStorage
{
    private static readonly byte[] Magic = "EWPDOC1\0"u8.ToArray();
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Uploads are limited to 25 MB (DocumentsController), so a document is sealed in memory.</summary>
    private const int MaxDocumentBytes = 25 * 1024 * 1024;

    public async Task<StoredDocument> StoreAsync(Guid documentId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        var plaintext = await ReadAllAsync(content, cancellationToken);
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var associated = Associated(documentId);
            var (keyId, keyEncryptionKey) = keys.Current;

            var wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var wrappedKey = new byte[KeySize];
            var wrapTag = new byte[TagSize];
            using (var wrap = new AesGcm(keyEncryptionKey, TagSize))
                wrap.Encrypt(wrapNonce, dataKey, wrappedKey, wrapTag, associated);

            var contentNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var contentTag = new byte[TagSize];
            using (var seal = new AesGcm(dataKey, TagSize))
                seal.Encrypt(contentNonce, plaintext, ciphertext, contentTag, associated);

            using var file = new MemoryStream();
            var keyIdBytes = Encoding.ASCII.GetBytes(keyId);
            file.Write(Magic);
            file.WriteByte((byte)keyIdBytes.Length);
            file.Write(keyIdBytes);
            file.Write(wrapNonce);
            file.Write(wrappedKey);
            file.Write(wrapTag);
            file.Write(contentNonce);
            file.Write(contentTag);
            file.Write(ciphertext);
            file.Position = 0;

            // ".enc": the stored file says what it is; the document's type stays in its record.
            var stored = await inner.StoreAsync(documentId, Path.ChangeExtension(fileName, ".enc"), file, cancellationToken);
            return stored with { Size = plaintext.Length, ContentHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant() };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<Stream?> OpenReadAsync(string storageReference, CancellationToken cancellationToken)
    {
        await using var stored = await inner.OpenReadAsync(storageReference, cancellationToken);
        if (stored is null)
            return null;

        var file = await ReadAllAsync(stored, cancellationToken, MaxDocumentBytes + 1024);
        var reader = new HeaderReader(file, storageReference);

        if (!reader.Take(Magic.Length).SequenceEqual(Magic))
            throw new DocumentDecryptionException(storageReference, "it is not an encrypted EWP document (stored before encryption at rest? reset the document store)");

        var keyId = Encoding.ASCII.GetString(reader.Take(reader.Take(1)[0]));
        var wrapNonce = reader.Take(NonceSize);
        var wrappedKey = reader.Take(KeySize);
        var wrapTag = reader.Take(TagSize);
        var contentNonce = reader.Take(NonceSize);
        var contentTag = reader.Take(TagSize);
        var ciphertext = reader.Rest();

        var keyEncryptionKey = keys.Find(keyId)
            ?? throw new DocumentDecryptionException(storageReference, $"its key '{keyId}' is not in the key ring");
        var associated = Associated(DocumentIdOf(storageReference));
        var dataKey = new byte[KeySize];
        try
        {
            using (var unwrap = new AesGcm(keyEncryptionKey, TagSize))
                unwrap.Decrypt(wrapNonce, wrappedKey, wrapTag, dataKey, associated);

            var plaintext = new byte[ciphertext.Length];
            using (var open = new AesGcm(dataKey, TagSize))
                open.Decrypt(contentNonce, ciphertext, contentTag, plaintext, associated);
            return new MemoryStream(plaintext, writable: false);
        }
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the authentication tag does not match, the file was changed or belongs to another
        // │ document: refuse it.
        // └──────────────────────────────────────────────────────────────────────────────────────
        catch (AuthenticationTagMismatchException)
        {
            throw new DocumentDecryptionException(storageReference, "it was changed, or belongs to another document");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public Task DeleteAsync(string storageReference, CancellationToken cancellationToken) =>
        inner.DeleteAsync(storageReference, cancellationToken);

    private static byte[] Associated(Guid documentId) => Encoding.ASCII.GetBytes($"ewp-document:{documentId:N}");

    /// <summary>The storage reference is "{documentId:N}/content.enc" (LocalFileSystemDocumentStorage).</summary>
    private static Guid DocumentIdOf(string storageReference) =>
        Guid.TryParseExact(storageReference.Split('/')[0], "N", out var id)
            ? id
            : throw new DocumentDecryptionException(storageReference, "its path does not name a document");

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken cancellationToken, int limit = MaxDocumentBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidOperationException("The document is larger than the storage accepts.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed class HeaderReader(byte[] file, string storageReference)
    {
        private int _position;

        public byte[] Take(int count)
        {
            if (_position + count > file.Length)
                throw new DocumentDecryptionException(storageReference, "its header is incomplete");
            var part = file.AsSpan(_position, count).ToArray();
            _position += count;
            return part;
        }

        public byte[] Rest() => file.AsSpan(_position).ToArray();
    }
}

/// <summary>
/// The key-encryption keys (configuration "DocumentStorage:Encryption"): the CURRENT one seals new
/// documents; every listed one still opens the documents it sealed (rotation = add a key, make it
/// current, keep the old one until no document names it). In production the keys live in a key
/// vault / HSM and are never in a file; here they come from configuration (Development only).
/// </summary>
public sealed class DocumentKeyRing
{
    private readonly Dictionary<string, byte[]> _keys;

    public DocumentKeyRing(string currentKeyId, IReadOnlyDictionary<string, string> base64Keys)
    {
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, value) in base64Keys)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new InvalidOperationException($"DocumentStorage:Encryption:Keys - '{id}' is not a valid key ID (ASCII letters, digits, - _ .; at most 64).");
            byte[] key;
            try { key = Convert.FromBase64String(value ?? string.Empty); }
            catch (FormatException) { throw new InvalidOperationException($"DocumentStorage:Encryption:Keys:{id} is not Base64."); }
            if (key.Length != 32)
                throw new InvalidOperationException($"DocumentStorage:Encryption:Keys:{id} must be 32 bytes (256 bits), Base64-encoded.");
            _keys[id] = key;
        }

        if (string.IsNullOrWhiteSpace(currentKeyId) || !_keys.TryGetValue(currentKeyId, out var current))
            throw new InvalidOperationException("DocumentStorage:Encryption:CurrentKeyId must name one of DocumentStorage:Encryption:Keys.");
        Current = (currentKeyId, current);
    }

    public (string KeyId, byte[] Key) Current { get; }

    public byte[]? Find(string keyId) => _keys.GetValueOrDefault(keyId);
}

/// <summary>A stored document cannot be opened: wrong or missing key, tampered file, or not encrypted.</summary>
public sealed class DocumentDecryptionException(string storageReference, string reason)
    : Exception($"Stored document '{storageReference}' cannot be opened: {reason}.");