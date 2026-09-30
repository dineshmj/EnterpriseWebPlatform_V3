using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.UploadDocument;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocument;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocuments;

namespace EnterpriseWebPlatform.DocumentsManagement.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddDocumentsManagementApplication(
        this IServiceCollection services)
    {
        services.AddScoped<UploadDocumentCommandHandler>();
        services.AddScoped<GetDocumentQueryHandler>();
        services.AddScoped<GetDocumentsQueryHandler>();

        return services;
    }
}