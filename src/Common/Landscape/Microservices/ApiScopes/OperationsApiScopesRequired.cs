namespace EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

public static class OperationsApiScopesRequired
{
    /// <summary>Read the platform's health (Operations Journey API) - only in a token exchanged by the Operations web BFF.</summary>
    public const string OPERATIONS_JOURNEY_READ =
        "operations-journey.read";

    /// <summary>Change a simulator's behaviour (Development only) - only in a token exchanged by the Operations web BFF.</summary>
    public const string OPERATIONS_JOURNEY_WRITE =
        "operations-journey.write";
}