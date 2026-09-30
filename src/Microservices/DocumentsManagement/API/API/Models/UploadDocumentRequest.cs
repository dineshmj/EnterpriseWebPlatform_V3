namespace EnterpriseWebPlatform.DocumentsManagement.API.Models;

public sealed class UploadDocumentRequest
{
    public IFormFile? File { get; init; }
}