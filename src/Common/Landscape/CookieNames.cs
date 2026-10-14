namespace EnterpriseWebPlatform.Common.Landscape;

// ┌─ :: SECURITY :: __Host- cookie names ──────────────────────────────────────────────────────────
// │ Why : A cookie that a sibling sub-domain can set, or that travels over plain HTTP, can be
// │       planted or stolen (session fixation, cookie tossing).
// │ How : Every session cookie is __Host- prefixed: the browser accepts it only with Secure, Path=/
// │       and no Domain, so it is locked to its own host over HTTPS; HttpOnly and SameSite=Lax are
// │       set where the cookie is issued.
// │ Ref : Features 2.3.3 | RFC 6265bis cookie prefixes | OWASP Session Management Cheat Sheet |
// │       CWE-614, CWE-1004
// └────────────────────────────────────────────────────────────────────────────────────────────────
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