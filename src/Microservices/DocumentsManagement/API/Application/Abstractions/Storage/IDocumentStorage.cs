namespace EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;

public interface IDocumentStorage
{
    Task<StoredDocument> StoreAsync(
        Guid documentId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(
        string storageReference,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string storageReference,
        CancellationToken cancellationToken);
}

public sealed record StoredDocument(
    string StorageReference,
    long Size,
    string ContentHash);