using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

using Duende.IdentityServer.Events;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Customer;

// ┌─ :: SECURITY :: The one-time code: registration or second factor ──────────────────────────────
// │ Why : The code is the proof of the mobile on record. Accepting it twice, counting tries in the
// │       browser, or leaving half a registration behind would each open a gap.
// │ How : The row is claimed once (CustomerOneTimeCodes); a wrong sign-in code also counts towards
// │       the sign-in's lockout. Registration: the password rule is checked BEFORE the code is
// │       spent; the sign-in is created first (the unique customer number stops a double
// │       registration), then Customer Onboarding links it - if that is refused or unreachable the
// │       new sign-in is removed again (no orphan on either side).
// │ Ref : Features 2.1.6 | ASVS V2.7, V2.1 | NIST SP 800-63B §5.1.3 | Threat-Model R-17
// └────────────────────────────────────────────────────────────────────────────────────────────────
[SecurityHeaders]
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class CodeModel(
    CustomerSignIn signIn,
    CustomerOneTimeCodes codes,
    CustomerLoginStore logins,
    CustomerDirectory directory,
    IEventService events,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    ILogger<CodeModel> logger) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public CustomerCodePurpose Purpose { get; private set; }

    /// <summary>Sign-in only: the last digits of the mobile the code went to (a registration says nothing - see Register).</summary>
    public string? MobileEnding { get; private set; }

    /// <summary>Development only (CustomerSignIn:ShowCodesOnPage): the code, in place of the SMS.</summary>
    public string? DevelopmentCode { get; private set; }

    /// <summary>The authorization request to return to (for "Cancel" / "start again").</summary>
    public string ReturnUrl { get; private set; } = "~/";

    /// <summary>The attempt has ended (code spent, or the sign-in locked): only "start again" is offered.</summary>
    public bool Ended { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        var (pending, open) = await LoadAsync();
        if (pending is null)
            return RedirectToPage("/Customer/Login");
        if (open is null)
            return EndWith("This code has expired or was used up. Please start again.");
        Describe(pending, open);
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var cancellationToken = HttpContext.RequestAborted;
        var (pending, open) = await LoadAsync();
        if (pending is null)
            return RedirectToPage("/Customer/Login");
        if (open is null)
            return EndWith("This code has expired or was used up. Please start again.");
        Describe(pending, open);

        if (open.Purpose == CustomerCodePurpose.Registration &&
            CustomerLoginStore.PasswordProblem(open.CustomerNumber, Password, ConfirmPassword) is { } problem)
        {
            ModelState.AddModelError(string.Empty, problem);
            return Page();
        }

        var check = await codes.CheckAsync(open, Code, cancellationToken);
        if (check == CodeCheck.Spent)
            return EndWith("Too many wrong codes, or the code has expired. Please start again.");

        if (check == CodeCheck.Wrong)
        {
            if (open.Purpose == CustomerCodePurpose.SignIn && open.SubjectId is { } subject)
            {
                await logins.CountFailureAsync(subject, cancellationToken);
                await events.RaiseAsync(new UserLoginFailureEvent(open.CustomerNumber, "invalid one-time code"), cancellationToken);
                if (await logins.IsLockedAsync(subject, cancellationToken))
                    return EndWith("Too many wrong attempts: online access is locked for 15 minutes. Please wait, then sign in again.");
            }

            ModelState.AddModelError(string.Empty, "That code is not right. Check the latest SMS and try again.");
            return Page();
        }

        return open.Purpose == CustomerCodePurpose.SignIn
            ? await CompleteSignInAsync(pending, open, cancellationToken)
            : await CompleteRegistrationAsync(pending, open, cancellationToken);
    }

    private async Task<IActionResult> CompleteSignInAsync(PendingCustomerCode pending, CustomerOneTimeCode open, CancellationToken cancellationToken)
    {
        var login = open.SubjectId is { } subject ? await logins.FindBySubjectIdAsync(subject.ToString(), cancellationToken) : null;
        if (login is null || !login.IsActive || login.LockoutEnd > time.GetUtcNow())
            return EndWith("Online access is not available for this sign-in. Please start again.");

        return await signIn.CompleteAsync(this, login, pending.ReturnUrl, ["pwd", "sms", "mfa"]);
    }

    private async Task<IActionResult> CompleteRegistrationAsync(PendingCustomerCode pending, CustomerOneTimeCode open, CancellationToken cancellationToken)
    {
        var login = new CustomerLogin
        {
            SubjectId = Guid.NewGuid(),
            CustomerNumber = open.CustomerNumber,
            DisplayName = open.DisplayName,
            MobileEnding = open.MobileEnding
        };

        if (!await logins.CreateAsync(login, Password, cancellationToken))
            return EndWith("This customer already has online access. Please sign in.");

        var linked = await directory.LinkAsync(open.CustomerNumber, open.Email ?? string.Empty, login.SubjectId, cancellationToken);
        if (linked != LinkResult.Linked)
        {
            await logins.RemoveAsync(login.SubjectId, cancellationToken);
            logger.LogWarning("Online registration for {CustomerNumber} not completed: Customer Onboarding answered {Result}.", open.CustomerNumber, linked);
            return EndWith(linked == LinkResult.Unavailable
                ? "Online registration is not available right now. Please try again later."
                : "We could not complete the registration. Please visit your branch.");
        }

        logger.LogInformation("Customer {CustomerNumber} registered for online access (subject {SubjectId}).", login.CustomerNumber, login.SubjectId);

        // The person proved the mobile on record (the code) and chose the password: two factors.
        return await signIn.CompleteAsync(this, login, pending.ReturnUrl, ["pwd", "sms", "mfa"]);
    }

    private async Task<(PendingCustomerCode? Pending, CustomerOneTimeCode? Open)> LoadAsync()
    {
        var pending = PendingCustomerCode.Load(HttpContext, dataProtection, time);
        if (pending is null)
            return (null, null);
        ReturnUrl = pending.ReturnUrl;
        return (pending, await codes.FindOpenAsync(pending.CodeId, pending.Purpose, HttpContext.RequestAborted));
    }

    private void Describe(PendingCustomerCode pending, CustomerOneTimeCode open)
    {
        Purpose = open.Purpose;
        MobileEnding = open.Purpose == CustomerCodePurpose.SignIn ? open.MobileEnding : null;
        DevelopmentCode = pending.DevelopmentCode;
    }

    private PageResult EndWith(string message)
    {
        PendingCustomerCode.Clear(HttpContext);
        ModelState.AddModelError(string.Empty, message);
        Ended = true;
        return Page();
    }
}