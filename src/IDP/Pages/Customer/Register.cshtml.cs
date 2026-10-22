using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Customer;

// ┌─ :: SECURITY :: Online registration, step 1: who are you? ─────────────────────────────────────
// │ Why : Anyone can type a customer number. The page must not confirm which numbers exist, which
// │       e-mail addresses belong to them, or who already has online access.
// │ How : Customer Onboarding answers "match" or nothing; EVERY outcome leads to the same code page
// │       with the same words - a code row is created even when nothing matched, but no code is
// │       sent, so no typed code can ever pass; the per-IP throttle limits the probing.
// │ Ref : Features 2.1.6 | OWASP A07 (account enumeration) | ASVS V2.5.1 | Threat-Model R-17
// └────────────────────────────────────────────────────────────────────────────────────────────────
[SecurityHeaders]
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class RegisterModel(
    CustomerSignIn signIn,
    CustomerLoginStore logins,
    CustomerDirectory directory) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        public string CustomerNumber { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string ReturnUrl { get; set; } = "~/";
    }

    public async Task<IActionResult> OnGet(string? returnUrl)
    {
        var context = await signIn.ContextOfAsync(returnUrl, HttpContext.RequestAborted);
        if (context is not null && !context.Client.ServesCustomers())
            return RedirectToPage("/Account/Login", new { returnUrl });

        Input = new InputModel { ReturnUrl = CustomerSignIn.SafeReturnUrl(context, returnUrl) };
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var cancellationToken = HttpContext.RequestAborted;
        var context = await signIn.ContextOfAsync(Input.ReturnUrl, cancellationToken);
        if (context is not null && !context.Client.ServesCustomers())
            return RedirectToPage("/Account/Login", new { returnUrl = Input.ReturnUrl });
        var returnUrl = CustomerSignIn.SafeReturnUrl(context, Input.ReturnUrl);

        var number = CustomerLoginStore.Normalize(Input.CustomerNumber);
        var email = (Input.Email ?? string.Empty).Trim();
        if (number.Length is 0 or > 20 || email.Length is 0 or > 254)
        {
            ModelState.AddModelError(string.Empty, "Enter your customer number and your e-mail address.");
            return Page();
        }

        RegistrationCandidate? candidate;
        try
        {
            candidate = await logins.ExistsAsync(number, cancellationToken)
                ? null
                : await directory.FindCandidateAsync(number, email, cancellationToken);
        }
        catch (CustomerDirectoryUnavailableException)
        {
            ModelState.AddModelError(string.Empty, "Online registration is not available right now. Please try again later.");
            return Page();
        }

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If nothing matched, still create a code row and show the same page - but send no
        // │ code. The person cannot tell this from a match; no code can complete it.
        // └──────────────────────────────────────────────────────────────────────────────────────
        return await signIn.SendCodeAsync(this, new CustomerOneTimeCode
        {
            Purpose = CustomerCodePurpose.Registration,
            CustomerNumber = number,
            Email = email,
            DisplayName = candidate?.DisplayName ?? "-",
            MobileEnding = candidate?.MobileEnding ?? "---"
        }, returnUrl, delivered: candidate is not null);
    }
}