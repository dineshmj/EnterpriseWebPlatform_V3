using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseWebPlatform.Audit.Api.Infrastructure;

// ┌─ :: SECURITY :: Signed checkpoints outside the database ───────────────────────────────────────
// │ Why : Someone with full database access could rewrite the whole chain and recompute every hash.
// │ How : The newest verified entry is anchored in a checkpoint signed with an ECDSA P-256 key and
// │       stored OUTSIDE the database; a rewritten chain no longer matches its anchors, and a new
// │       anchor cannot be forged without the key.
// │ Ref : Features 2.4.8 | Threat-Model (audit anchor) | FIPS 186-5 (ECDSA)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// A signed statement, kept OUTSIDE the audit database: "at this moment, entry {Sequence} of the
/// trail had hash {EntryHash}". The hash chain makes every edit inside the database visible -
/// except a complete rewrite by someone who can disable the trigger and recompute every hash.
/// A checkpoint closes that gap: the rewritten chain no longer carries the anchored hash at the
/// anchored position, and a new checkpoint cannot be forged without the signing key, which the
/// database never sees.
/// </summary>
public sealed record AuditCheckpoint(long Sequence, string EntryHash, DateTimeOffset AnchoredAt, string KeyId, string Signature)
{
    /// <summary>The exact bytes that are signed.</summary>
    public static byte[] SignedContent(long sequence, string entryHash, DateTimeOffset anchoredAt) =>
        Encoding.UTF8.GetBytes($"ewp-audit-checkpoint|v1|{sequence}|{entryHash}|{anchoredAt.UtcDateTime:O}");
}

/// <summary>
/// Signs and verifies checkpoints with ECDSA P-256 (configuration "Audit:Anchors:SigningKey",
/// a PKCS#8 private key in Base64, and "Audit:Anchors:KeyId"). An external verifier needs only
/// the public key. In production the key lives in a key vault / HSM that the database
/// administrators cannot use; here it comes from configuration (Development only).
/// </summary>
public sealed class AuditCheckpointSigner : IDisposable
{
    private readonly ECDsa _key;

    public AuditCheckpointSigner(string keyId, string pkcs8Base64)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new InvalidOperationException("Audit:Anchors:KeyId is not configured.");
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the signing key is missing, refuse to start - the chain would have no anchor
        // │ outside its database (fail closed).
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(pkcs8Base64))
            throw new InvalidOperationException("Audit:Anchors:SigningKey is not configured: the audit chain would have no anchor outside its database.");

        _key = ECDsa.Create();
        try
        {
            _key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            _key.Dispose();
            throw new InvalidOperationException("Audit:Anchors:SigningKey is not a Base64 PKCS#8 ECDSA private key.", ex);
        }

        if (_key.KeySize != 256)
        {
            _key.Dispose();
            throw new InvalidOperationException("Audit:Anchors:SigningKey must be an ECDSA P-256 key.");
        }

        KeyId = keyId;
    }

    public string KeyId { get; }

    public AuditCheckpoint Sign(long sequence, string entryHash, DateTimeOffset anchoredAt)
    {
        var signature = _key.SignData(AuditCheckpoint.SignedContent(sequence, entryHash, anchoredAt), HashAlgorithmName.SHA256);
        return new AuditCheckpoint(sequence, entryHash, anchoredAt, KeyId, Convert.ToBase64String(signature));
    }

    public bool IsValid(AuditCheckpoint checkpoint)
    {
        if (!string.Equals(checkpoint.KeyId, KeyId, StringComparison.Ordinal))
            return false;
        try
        {
            return _key.VerifyData(
                AuditCheckpoint.SignedContent(checkpoint.Sequence, checkpoint.EntryHash, checkpoint.AnchoredAt),
                Convert.FromBase64String(checkpoint.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Where checkpoints are kept: write-once files ("Audit:Anchors:Directory"), one per checkpoint,
/// created new and then marked read-only - never overwritten. In production this is immutable
/// storage under a different administrator (e.g. S3 Object Lock or an Azure immutable blob
/// container), so that one person cannot change both the trail and its anchors.
/// </summary>
public sealed class AuditAnchorStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _directory;

    public AuditAnchorStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Audit:Anchors:Directory is not configured.");
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task AppendAsync(AuditCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, $"checkpoint-{checkpoint.Sequence:D12}.json");
        if (File.Exists(path))
            return;   // this sequence is anchored already

        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(file, checkpoint, Json, cancellationToken);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }

    /// <summary>Every checkpoint, oldest first. A file that cannot be read is returned as an invalid checkpoint, never skipped.</summary>
    public async Task<IReadOnlyList<AuditCheckpoint>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var checkpoints = new List<AuditCheckpoint>();
        foreach (var path in Directory.EnumerateFiles(_directory, "checkpoint-*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                await using var file = File.OpenRead(path);
                checkpoints.Add(await JsonSerializer.DeserializeAsync<AuditCheckpoint>(file, Json, cancellationToken)
                    ?? throw new JsonException("empty"));
            }
            catch (JsonException)
            {
                var sequence = long.TryParse(Path.GetFileNameWithoutExtension(path)["checkpoint-".Length..], out var s) ? s : 0;
                checkpoints.Add(new AuditCheckpoint(sequence, string.Empty, DateTimeOffset.MinValue, string.Empty, string.Empty));
            }
        }
        return checkpoints.OrderBy(c => c.Sequence).ToList();
    }

    public async Task<long> LatestSequenceAsync(CancellationToken cancellationToken) =>
        (await ReadAllAsync(cancellationToken)).Select(c => c.Sequence).DefaultIfEmpty(0).Max();
}