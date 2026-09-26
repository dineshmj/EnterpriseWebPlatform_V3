using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    public static IServiceCollection AddDocumentsManagementPersistence(
        this IServiceCollection services)
    {
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        return services;
    }
}
