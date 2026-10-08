using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

using Duende.IdentityServer.Services;
using QRCoder;

using EnterpriseWebPlatform.IdentityServer.Repositories;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Account;

/// <summary>
/// Enrolment, the first time a person signs in while MFA is on: scan the QR code with Google
/// Authenticator (or type the key), prove it works with one code, keep the recovery codes, done.
/// The secret is saved - encrypted - only after the code proves the app has it.
/// </summary>
[SecurityHeaders]
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class MfaSetupModel(
    IIdentityServerInteractionService interaction,
    IEventService events,
    IUserRepository users,
    MfaService mfa,
    IOptions<MfaOptions> options,
    IDataProtectionProvider dataProtection,
    TimeProvider time) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    /// <summary>The key for typing in by hand, in groups of four.</summary>
    public string ManualKey { get; private set; } = string.Empty;

    public string AccountName { get; private set; } = string.Empty;

    /// <summary>Shown once, right after a successful enrolment.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        var pending = await EnsureSecretAsync();
        if (pending is null)
            return RedirectToPage("/Account/Login");
        return Page();
    }

    /// <summary>The QR code (PNG, served by this page - the IDP's CSP allows only its own images).</summary>
    public async Task<IActionResult> OnGetQrCode()
    {
        var pending = PendingSignIn.Load(HttpContext, dataProtection, time);
        if (pending?.EnrolmentSecret is null)
            return NotFound();

        var user = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var uri = Totp.OtpAuthUri(options.Value.Issuer, user.UserName, Convert.FromBase64String(pending.EnrolmentSecret));
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        Response.Headers.CacheControl = "no-store";
        return File(new PngByteQRCode(data).GetGraphic(6), "image/png");
    }

    public async Task<IActionResult> OnPost()
    {
        var pending = await EnsureSecretAsync();
        if (pending is null)
            return RedirectToPage("/Account/Login");

        var user = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        if (user is null || !user.IsActive)
            return RedirectToPage("/Account/Login");

        var codes = await mfa.ConfirmEnrolmentAsync(user.Id, Convert.FromBase64String(pending.EnrolmentSecret!), Code ?? string.Empty, HttpContext.RequestAborted);
        if (codes is null)
        {
            ModelState.AddModelError(string.Empty, "That code does not match. Check that the phone's time is set automatically, then enter the current code.");
            Code = string.Empty;
            return Page();
        }

        (pending with { EnrolmentSecret = null, EnrolmentConfirmed = true }).Save(HttpContext, dataProtection);
        RecoveryCodes = codes.Select(MfaService.Display).ToList();
        return Page();
    }

    /// <summary>"I have saved my recovery codes": the sign-in completes, with MFA.</summary>
    public async Task<IActionResult> OnPostContinue()
    {
        var pending = PendingSignIn.Load(HttpContext, dataProtection, time);
        if (pending is not { EnrolmentConfirmed: true })
            return RedirectToPage("/Account/Login");

        var user = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        if (user is null || !user.IsActive)
            return RedirectToPage("/Account/Login");

        return await SignInCompletion.CompleteAsync(this, interaction, events, user, pending.RememberLogin, pending.ReturnUrl, withMfa: true);
    }

    /// <summary>The pending sign-in with a new secret for this enrolment (created once, kept in the encrypted cookie).</summary>
    private async Task<PendingSignIn?> EnsureSecretAsync()
    {
        var pending = PendingSignIn.Load(HttpContext, dataProtection, time);
        if (pending is null || pending.EnrolmentConfirmed)
            return null;

        if (pending.EnrolmentSecret is null)
        {
            pending = pending with { EnrolmentSecret = Convert.ToBase64String(Totp.NewSecret()) };
            pending.Save(HttpContext, dataProtection);
        }

        var user = await users.FindBySubjectIdAsync(pending.SubjectId, HttpContext.RequestAborted);
        AccountName = $"{options.Value.Issuer}: {user?.UserName}";
        var key = Totp.Base32(Convert.FromBase64String(pending.EnrolmentSecret));
        ManualKey = string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
        return pending;
    }
}