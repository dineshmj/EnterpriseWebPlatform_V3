namespace EnterpriseWebPlatform.Common.Landscape;

public static class CookieNames
{
	public const string BSS_SHELL_HOST_BFF = "__Host-BSS-Shell-bff";								// Cookie names must start with __Host- so that OIDC logout can work correctly.

	public const string MICROSERVICE_CUSTOMER_ONBOARDING_HOST_BFF = "__Host-Microservice-Customer-Onboarding-bff";

	public const string MICROSERVICE_COMPLIANCE_HOST_BFF = "__Host-Microservice-Compliance-bff";

	public const string MICROSERVICE_ACCOUNTS_HOST_BFF = "__Host-Microservice-Accounts-bff";

	public const string MICROSERVICE_PAYMENTS_HOST_BFF = "__Host-Microservice-Payments-bff";

	// Only the .NET BFFs read these constants. The KYC BFF is NestJS and names its own
	// session cookie (__Host-KYC-BFF-SESSION, see its main.ts).
}