namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.UploadDocument;

public sealed record UploadDocumentCommand(
    string FileName,
    string ContentType,
    Stream Content);
