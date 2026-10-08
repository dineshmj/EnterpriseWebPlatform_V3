using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

using Duende.IdentityServer.Events;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Repositories;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Account;

/// <summary>
/// The second factor: the 6-digit code from Google Authenticator (or a recovery code). Reached
/// only with a pending sign-in (correct password, MFA on); the person is signed in only here.
/// </summary>
[SecurityHeaders]
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class MfaModel(
    IIdentityServerInteractionService interaction,
    IEventService events,
    IUserRepository users,
    MfaService mfa,
    IDataProtectionProvider dataProtection,
    TimeProvider time) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    public IActionResult OnGet() =>
        PendingSignIn.Load(HttpContext, dataProtection, time) is null ? RedirectToPage("/Account/Login") : Page();

    public async Task<IActionResult> OnPost()
    {
        var pending = PendingSignIn.Load(HttpContext, dataProtection, time);
        if (pending is null)
            return RedirectToPage("/Account/Login");

        var user = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        if (user is null || !user.IsActive)
            return RedirectToPage("/Account/Login");

        if (await mfa.VerifyAsync(user, Code ?? string.Empty, HttpContext.RequestAborted))
            return await SignInCompletion.CompleteAsync(this, interaction, events, user, pending.RememberLogin, pending.ReturnUrl, withMfa: true);

        await events.RaiseAsync(new UserLoginFailureEvent(user.UserName, "invalid authenticator code"), HttpContext.RequestAborted);
        ModelState.AddModelError(string.Empty, "That code is not valid. Enter the current 6-digit code from Google Authenticator, or a recovery code.");
        Code = string.Empty;
        return Page();
    }
}