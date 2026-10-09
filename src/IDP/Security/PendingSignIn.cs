using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer;
using Duende.IdentityServer.Events;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Security;

/// <summary>
/// The state between "password correct" and "signed in" while MFA is on: who, where to go
/// afterwards, and - during enrolment - the new secret. Kept in a short-lived (5 minutes),
/// encrypted, HttpOnly cookie; the person is NOT signed in until the code is verified.
/// </summary>
public sealed record PendingSignIn(
    string SubjectId,
    bool RememberLogin,
    string ReturnUrl,
    DateTimeOffset ExpiresAt,
    string? EnrolmentSecret = null,
    bool EnrolmentConfirmed = false)
{
    private const string CookieName = "__Host-ewp.mfa.pending";
    private const string Purpose = "EnterpriseWebPlatform.IdentityServer.Mfa.PendingSignIn.v1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public void Save(HttpContext http, IDataProtectionProvider dataProtection) =>
        http.Response.Cookies.Append(CookieName,
            dataProtection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(this)),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = Lifetime });

    /// <summary>The pending sign-in of this browser; null when none, tampered with, or expired.</summary>
    public static PendingSignIn? Load(HttpContext http, IDataProtectionProvider dataProtection, TimeProvider time)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var sealedValue))
            return null;
        try
        {
            var pending = JsonSerializer.Deserialize<PendingSignIn>(dataProtection.CreateProtector(Purpose).Unprotect(sealedValue));
            return pending is not null && pending.ExpiresAt > time.GetUtcNow() ? pending : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear(HttpContext http) => http.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, Path = "/" });
}

/// <summary>
/// The last step of every sign-in: the login event, the IDP session cookie - with the methods
/// used ("amr": "pwd", or "pwd otp mfa" after the authenticator code), which the access tokens
/// carry so the APIs can require MFA for risky actions - and the redirect back to the client.
/// </summary>
public static class SignInCompletion
{
    public static readonly TimeSpan RememberLoginLifetime = TimeSpan.FromHours(8);

    public static async Task<IActionResult> CompleteAsync(
        PageModel page,
        IIdentityServerInteractionService interaction,
        IEventService events,
        User user,
        bool rememberLogin,
        string? returnUrl,
        bool withMfa)
    {
        var http = page.HttpContext;
        var displayName = $"{user.FirstName} {user.LastName}";
        await events.RaiseAsync(new UserLoginSuccessEvent(user.UserName, user.SubjectId.ToString(), displayName), http.RequestAborted);

        // "Keep me signed in" survives closing the browser, but only for a working day - a staff
        // sign-in to a bank must not live for weeks on a shared or lost device.
        var properties = rememberLogin
            ? new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.Add(RememberLoginLifetime) }
            : null;

        var identityServerUser = new IdentityServerUser(user.SubjectId.ToString())
        {
            DisplayName = displayName,
            AuthenticationMethods = withMfa ? ["pwd", "otp", "mfa"] : ["pwd"]
        };
        await http.SignInAsync(identityServerUser, properties);
        PendingSignIn.Clear(http);

        var context = await interaction.GetAuthorizationContextAsync(returnUrl, http.RequestAborted);
        if (context is not null)
            return context.IsNativeClient() ? page.LoadingPage(returnUrl!) : new RedirectResult(returnUrl!);

        if (page.Url.IsLocalUrl(returnUrl))
            return new RedirectResult(returnUrl!);
        if (string.IsNullOrEmpty(returnUrl))
            return new RedirectResult("~/");

        throw new ArgumentException("Invalid return URL.");
    }
}