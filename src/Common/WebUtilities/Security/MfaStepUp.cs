using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

/// <summary>
/// Step-up for risky actions (an officer's decision, an operations retry): when MFA is switched
/// on ("Mfa:Enabled"), the person must have signed in WITH their authenticator code - the token's
/// "amr" (authentication methods, RFC 8176) contains "mfa". When MFA is off, the requirement is met.
/// </summary>
public sealed class MfaStepUpRequirement : IAuthorizationRequirement;

public sealed class MfaStepUpAuthorizationHandler(IConfiguration configuration) : AuthorizationHandler<MfaStepUpRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaStepUpRequirement requirement)
    {
        if (!MfaStepUp.IsEnabled(configuration) ||
            context.User.FindAll("amr").Any(c => string.Equals(c.Value, "mfa", StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class MfaStepUp
{
    public const string EnabledKey = "Mfa:Enabled";
    public const string ErrorCode = "mfa_required";

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>(EnabledKey);

    /// <summary>Adds the step-up requirement to a policy (no effect while MFA is off).</summary>
    public static AuthorizationPolicyBuilder RequireMfa(this AuthorizationPolicyBuilder policy) =>
        policy.AddRequirements(new MfaStepUpRequirement());

    /// <summary>
    /// Registers the handler, and answers a refusal caused by missing MFA with a clear 403 (code
    /// "mfa_required") instead of a bare one - and deliberately not 401, which would make the
    /// screens sign in again silently, without the code, and fail again.
    /// </summary>
    public static IServiceCollection AddEwpMfaStepUp(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, MfaStepUpAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, MfaAwareAuthorizationResultHandler>();
        return services;
    }

    private sealed class MfaAwareAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
        {
            if (result.Forbidden &&
                result.AuthorizationFailure?.FailedRequirements.OfType<MfaStepUpRequirement>().Any() == true &&
                result.AuthorizationFailure.FailedRequirements.All(r => r is MfaStepUpRequirement))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                // The screens show "error" (then "detail") as-is, so it carries the readable sentence.
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "https://datatracker.ietf.org/doc/html/rfc9470",
                    title = "Authenticator code required",
                    status = 403,
                    code = ErrorCode,
                    error = "This action needs a sign-in with your authenticator code: sign out, then sign in again and enter the code from Google Authenticator.",
                    detail = "Sign out, then sign in again and enter the code from Google Authenticator."
                }));
                return;
            }

            await _default.HandleAsync(next, context, policy, result);
        }
    }
}