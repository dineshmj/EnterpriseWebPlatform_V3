using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

using Duende.IdentityServer.Events;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Customer;

// ┌─ :: SECURITY :: The customers' sign-in page ───────────────────────────────────────────────────
// │ Why : Customers sign in from the internet to their own channel; the page must not serve a staff
// │       client, must not reveal which customer numbers have online access, and - with MFA on -
// │       must not sign anybody in on the password alone.
// │ How : Only for a customer client's request (a staff client's request goes to the staff page);
// │       one answer for every failure; lockout and the per-IP throttle; MFA on = a one-time code to
// │       the mobile on record follows (amr pwd sms mfa).
// │ Ref : Features 2.1.6 | OWASP A07 | ASVS V2.2 | NIST SP 800-63B | Threat-Model R-17
// └────────────────────────────────────────────────────────────────────────────────────────────────
[SecurityHeaders]
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(
    CustomerSignIn signIn,
    CustomerLoginStore logins,
    IIdentityServerInteractionService interaction,
    IEventService events,
    IOptions<MfaOptions> mfaOptions) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        public string CustomerNumber { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public string ReturnUrl { get; set; } = "~/";

        public string Button { get; set; } = string.Empty;
    }

    public string? ClientName { get; private set; }

    public async Task<IActionResult> OnGet(string? returnUrl)
    {
        var context = await signIn.ContextOfAsync(returnUrl, HttpContext.RequestAborted);
        if (context is not null && !context.Client.ServesCustomers())
            return RedirectToPage("/Account/Login", new { returnUrl });

        Input = new InputModel { ReturnUrl = CustomerSignIn.SafeReturnUrl(context, returnUrl) };
        ClientName = context?.Client.ClientName;
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var cancellationToken = HttpContext.RequestAborted;
        var context = await signIn.ContextOfAsync(Input.ReturnUrl, cancellationToken);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the request behind this form is a STAFF client's, refuse: a customer's password
        // │ never completes a staff sign-in (the staff page does the same the other way round).
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (context is not null && !context.Client.ServesCustomers())
            return RedirectToPage("/Account/Login", new { returnUrl = Input.ReturnUrl });

        ClientName = context?.Client.ClientName;
        var returnUrl = CustomerSignIn.SafeReturnUrl(context, Input.ReturnUrl);

        if (Input.Button != "login")
        {
            if (context is null)
                return RedirectToPage("/Customer/Done");
            await interaction.DenyAuthorizationAsync(context, InteractionError.AccessDenied, cancellationToken);
            return Redirect(returnUrl);
        }

        var login = await logins.ValidateCredentialsAsync(Input.CustomerNumber, Input.Password, cancellationToken);
        if (login is null)
        {
            await events.RaiseAsync(new UserLoginFailureEvent(CustomerLoginStore.Normalize(Input.CustomerNumber), "invalid credentials"), cancellationToken);
            ModelState.AddModelError(string.Empty, "Invalid customer number or password.");
            Input.Password = string.Empty;
            return Page();
        }

        // MFA off (Development only): the password signs the customer in. On: a code to the mobile.
        if (!mfaOptions.Value.Enabled)
            return await signIn.CompleteAsync(this, login, returnUrl, ["pwd"]);

        return await signIn.SendCodeAsync(this, new CustomerOneTimeCode
        {
            Purpose = CustomerCodePurpose.SignIn,
            CustomerNumber = login.CustomerNumber,
            SubjectId = login.SubjectId,
            DisplayName = login.DisplayName,
            MobileEnding = login.MobileEnding
        }, returnUrl, delivered: true);
    }
}