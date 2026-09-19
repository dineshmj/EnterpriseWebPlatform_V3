namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class UserRelationship
{
    public long Id { get; set; }

    public long SubjectUserId { get; set; }

    public string RelationshipType { get; set; } = null!;

    public string ResourceType { get; set; } = null!;

    public string ResourceId { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    // Relationships

    public User SubjectUser { get; set; } = null!;
}