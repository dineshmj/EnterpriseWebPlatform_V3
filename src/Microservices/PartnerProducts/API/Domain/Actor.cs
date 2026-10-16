namespace EnterpriseWebPlatform.PartnerProducts.Api.Domain;

/// <summary>The staff member at the counter, from their (delegated) token - never from the request body.</summary>
public sealed record Actor(string Subject, string LanId, string BranchCode);