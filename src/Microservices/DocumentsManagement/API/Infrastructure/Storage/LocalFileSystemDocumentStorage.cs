using System.Security.Cryptography;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;

namespace EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Storage;

public sealed class LocalFileSystemDocumentStorage : IDocumentStorage
{
    private readonly string _rootPath;

    public LocalFileSystemDocumentStorage(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<StoredDocument> StoreAsync(
        Guid documentId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        var relativePath = Path.Combine(
            documentId.ToString("N"),
            $"content{extension}");

        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var output = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            size += read;
        }

        await output.FlushAsync(cancellationToken);

        return new StoredDocument(
            relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            size,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public Task<Stream?> OpenReadAsync(
        string storageReference,
        CancellationToken cancellationToken)
    {
        var fullPath = GetFullPath(storageReference);

        if (!File.Exists(fullPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(
        string storageReference,
        CancellationToken cancellationToken)
    {
        var fullPath = GetFullPath(storageReference);

        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }

    private string GetFullPath(string storageReference)
    {
        var normalized = storageReference.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, normalized));

        if (!fullPath.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid storage reference.");

        return fullPath;
    }
}
