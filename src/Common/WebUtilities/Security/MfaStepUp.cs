using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

/// <summary>
/// Step-up for risky actions (an officer's decision, an operations retry): when MFA is switched
/// on ("Mfa:Enabled"), the person must have signed in WITH their authenticator code - the token's
/// "amr" (authentication methods, RFC 8176) contains "mfa" - and RECENTLY: the sign-in ("auth_time",
/// which a token refresh does not move) is at most "Mfa:StepUpMaxAgeMinutes" old (default 15). A
/// session that began with MFA in the morning does not approve a payment in the afternoon; a
/// token without auth_time is refused. When MFA is off, the requirement is met.
/// </summary>
public sealed class MfaStepUpRequirement : IAuthorizationRequirement;

public sealed class MfaStepUpAuthorizationHandler(IConfiguration configuration, TimeProvider? time = null) : AuthorizationHandler<MfaStepUpRequirement>
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaStepUpRequirement requirement)
    {
        if (!MfaStepUp.IsEnabled(configuration) ||
            (context.User.FindAll("amr").Any(c => string.Equals(c.Value, "mfa", StringComparison.Ordinal)) &&
             MfaStepUp.IsRecent(context.User.FindFirst("auth_time")?.Value, MfaStepUp.MaxAge(configuration), _time.GetUtcNow())))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class MfaStepUp
{
    public const string EnabledKey = "Mfa:Enabled";
    public const string MaxAgeKey = "Mfa:StepUpMaxAgeMinutes";
    public const string ErrorCode = "mfa_required";

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>(EnabledKey);

    /// <summary>How old the MFA sign-in may be for a risky action: "Mfa:StepUpMaxAgeMinutes", 1 to 60, default 15.</summary>
    public static TimeSpan MaxAge(IConfiguration configuration) =>
        TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue(MaxAgeKey, 15), 1, 60));

    /// <summary>auth_time (Unix seconds, OIDC Core) is no older than maxAge; a missing or malformed value is not recent.</summary>
    public static bool IsRecent(string? authTime, TimeSpan maxAge, DateTimeOffset now) =>
        long.TryParse(authTime, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
        now - DateTimeOffset.FromUnixTimeSeconds(seconds) <= maxAge &&
        DateTimeOffset.FromUnixTimeSeconds(seconds) <= now.AddMinutes(5);

    /// <summary>
    /// Fail closed: MFA may be off on a developer's machine only. Outside Development the host
    /// refuses to start unless "Mfa:Enabled" is explicitly true - a forgotten or mistyped
    /// setting can never silently switch off the second factor or the step-up. Called by the
    /// IDP and by every API that requires step-up, so all of them hold the same policy.
    /// </summary>
    public static WebApplicationBuilder RequireMfaOutsideDevelopment(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment() && !IsEnabled(builder.Configuration))
        {
            throw new InvalidOperationException(
                $"\"{EnabledKey}\" must be true outside Development ({builder.Environment.EnvironmentName}): MFA and the step-up for risky actions cannot be switched off there.");
        }

        return builder;
    }

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
                    title = "Recent authenticator code required",
                    status = 403,
                    code = ErrorCode,
                    error = "This action needs a recent confirmation with your authenticator code. Enter the code in the window that opens, then the action continues.",
                    detail = "Risky actions need a confirmation with the authenticator code within the last few minutes. The screen opens a window where you enter the code from Google Authenticator; you stay signed in."
                }));
                return;
            }

            await _default.HandleAsync(next, context, policy, result);
        }
    }
}