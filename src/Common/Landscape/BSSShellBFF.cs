namespace EnterpriseWebPlatform.Common.Landscape;

public static class BSSShellBFF
{
	public const string CLIENT_NAME_FOR_IDP = "BSS Shell BFF Client";
	public const string CLIENT_ID_FOR_IDP = "BSS.Shell.BFF.ClientID";
	public const string CLIENT_SECRET_FOR_IDP = "50485e4d-183f-465a-8234-fb4fb4316d09";         // Random GUID as secret; for demo purposes only.

	public const string SHELL_BFF_CLIENT_BASE_URL = "https://shell.dev.localhost:44367";

	// FIXME: Are these needed?
	public const string SHELL_BFF_TOKEN_ISSUER = "BSS Shell BFF Client";
	public const string SHELL_BFF_TOKEN_SIGNING_KEY = "990fdac7-be84-4ebe-85ff-038368d7ca19";       // Random GUID as signing key; for demo purposes only.
}