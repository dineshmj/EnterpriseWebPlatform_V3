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
        AllowShellOverlayFor(pending);
        return Page();
    }

    // Only a step-up's code page may be shown in the Shell's overlay (SecurityHeadersAttribute).
    private void AllowShellOverlayFor(PendingSignIn pending)
    {
        if (pending.StepUp)
            HttpContext.Items[SecurityHeadersAttribute.ShellMayFrameKey] = true;
    }

    public async Task<IActionResult> OnPost()
    {
        var pending = PendingSignIn.Load(HttpContext, dataProtection, time);
        if (pending is null)
            return RedirectToPage("/Account/Login");
        StepUp = pending.StepUp;
        AllowShellOverlayFor(pending);

        var user = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        if (user is null || !user.IsActive)
            return RedirectToPage("/Account/Login");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the code is right, complete the sign-in with amr "pwd otp mfa" and a new
        // │ auth_time; otherwise count a failure (lockout) and ask again.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (user.LockoutEnd > time.GetUtcNow())
            return LockedOut();

        if (await mfa.VerifyAsync(user, Code ?? string.Empty, HttpContext.RequestAborted))
            return await SignInCompletion.CompleteAsync(this, interaction, events, user, pending.RememberLogin, pending.ReturnUrl, withMfa: true);

        await events.RaiseAsync(new UserLoginFailureEvent(user.UserName, "invalid authenticator code"), HttpContext.RequestAborted);

        // The failure was just counted: if it was the last one allowed, the account is now locked.
        var reloaded = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        if (reloaded?.LockoutEnd > time.GetUtcNow())
            return LockedOut();

        ModelState.AddModelError(string.Empty, "That code is not valid. Enter the current 6-digit code from Google Authenticator, or a recovery code.");
        Code = string.Empty;
        return Page();
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Locked (5 wrong codes or passwords): the half-finished sign-in is ENDED - no further
    // │ code is even looked at - and the person is told plainly to wait. This person has
    // │ already proved the password, so saying "locked" reveals nothing to a stranger.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private PageResult LockedOut()
    {
        PendingSignIn.Clear(HttpContext);
        ModelState.AddModelError(string.Empty, "Too many wrong codes: this account is locked for 15 minutes. Wait, then sign in again - or ask an administrator.");
        Code = string.Empty;
        Locked = true;
        return Page();
    }

    /// <summary>The sign-in attempt ended because the account is locked: the page shows no code field.</summary>
    public bool Locked { get; private set; }
}