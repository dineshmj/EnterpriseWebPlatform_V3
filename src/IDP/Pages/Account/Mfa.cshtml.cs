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

// ┌─ :: SECURITY :: Second factor (TOTP) ──────────────────────────────────────────────────────────
// │ Why : A password alone is not enough for bank staff; a code must be fresh, single-use and not
// │       brute-forceable.
// │ How : Reached only with a pending sign-in from the password step; the code (or a recovery code)
// │       is checked against the encrypted secret, a used time step is never accepted again, wrong
// │       codes count towards the lockout.
// │ Ref : Features 2.1.3, 2.1.5 | RFC 6238 TOTP | OWASP A07 | ASVS V2.8 |
// │       NIST SP 800-63B (OTP authenticators)
// └────────────────────────────────────────────────────────────────────────────────────────────────
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

    /// <summary>A signed-in person confirming a risky action (no password was asked).</summary>
    public bool StepUp { get; private set; }

    public IActionResult OnGet()
    {
        var pending = PendingSignIn.Load(HttpContext, dataProtection, time);
        if (pending is null)
            return RedirectToPage("/Account/Login");

        StepUp = pending.StepUp;
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var pending = PendingSignIn.Load(HttpContext, dataProtection, time);
        if (pending is null)
            return RedirectToPage("/Account/Login");
        StepUp = pending.StepUp;

        var user = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        if (user is null || !user.IsActive)
            return RedirectToPage("/Account/Login");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the code is right, complete the sign-in with amr "pwd otp mfa" and a new
        // │ auth_time; otherwise count a failure (lockout) and ask again.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (await mfa.VerifyAsync(user, Code ?? string.Empty, HttpContext.RequestAborted))
            return await SignInCompletion.CompleteAsync(this, interaction, events, user, pending.RememberLogin, pending.ReturnUrl, withMfa: true);

        await events.RaiseAsync(new UserLoginFailureEvent(user.UserName, "invalid authenticator code"), HttpContext.RequestAborted);
        ModelState.AddModelError(string.Empty, "That code is not valid. Enter the current 6-digit code from Google Authenticator, or a recovery code.");
        Code = string.Empty;
        return Page();
    }
}