namespace EnterpriseWebPlatform.Common.Landscape;

public static class CookieNames
{
	public const string BSS_SHELL_HOST_BFF = "__Host-BSS-Shell-bff";								// Cookie names must start with __Host- so that OIDC logout can work correctly.
	public const string MICROSERVICE_CUSTOMER_ONBOARDING_HOST_BFF = "__Host-Microservice-Customer-Onboarding-bff";
	// FIXME: Shouldn't you be adding MFE cookie names for CustomerKyc, Accounts and Payments MFEs as well, or delete the above?
}