using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

using Duende.IdentityServer;
using Duende.IdentityServer.Events;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Data.Entities;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Customers;

/// <summary>The steps every customer page shares: where the person may go, the code "SMS", the sign-in itself.</summary>
public sealed class CustomerSignIn(
    IIdentityServerInteractionService interaction,
    IEventService events,
    CustomerOneTimeCodes codes,
    IDataProtectionProvider dataProtection,
    IOptions<CustomerSignInOptions> options,
    TimeProvider time,
    ILogger<CustomerSignIn> logger)
{
    /// <summary>
    /// The authorization request behind a return URL, or null for a direct visit. A staff
    /// client's request is not a customer's business: the caller sends it to the staff page.
    /// </summary>
    public Task<AuthorizationRequest?> ContextOfAsync(string? returnUrl, CancellationToken cancellationToken) =>
        interaction.GetAuthorizationContextAsync(returnUrl, cancellationToken);

    /// <summary>Where to go after the customer pages: the authorization request, or the IDP's own home - never elsewhere.</summary>
    public static string SafeReturnUrl(AuthorizationRequest? context, string? returnUrl) =>
        context is not null && !string.IsNullOrEmpty(returnUrl) ? returnUrl : "~/";

    // ┌─ SEC + DEBT ─────────────────────────────────────────────────────────────────────────
    // │ PoC: there is no SMS gateway. The code goes to the console (the mobile masked to its
    // │ last three digits - never the full number, never the code outside Development) and,
    // │ ONLY while CustomerSignIn:ShowCodesOnPage is on (Development), onto the page.
    // │ Production: an SMS / push provider receives the number from Customer Onboarding.
    // └──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Issues a code, "sends" it, and remembers it in this browser; returns the page to go to.</summary>
    public async Task<IActionResult> SendCodeAsync(PageModel page, CustomerOneTimeCode template, string returnUrl, bool delivered)
    {
        var (id, code) = await codes.IssueAsync(template, page.HttpContext.RequestAborted);
        var show = options.Value.ShowCodesOnPage;

        if (delivered)
        {
            logger.LogWarning("[DEMO SMS] To mobile ending {MobileEnding}: your EWP code is {Code} (valid {Minutes} minutes).",
                template.MobileEnding, show ? code : "******", (int)CustomerOneTimeCodes.Lifetime.TotalMinutes);
        }

        new PendingCustomerCode(id, template.Purpose, returnUrl, time.GetUtcNow().Add(CustomerOneTimeCodes.Lifetime),
                show && delivered ? code : null)
            .Save(page.HttpContext, dataProtection);
        return page.RedirectToPage("/Customer/Code");
    }

    /// <summary>
    /// Signs the customer in (the IDP session) with the methods used, and returns to the
    /// authorization request. No "keep me signed in": a customer's IDP session ends with the
    /// browser session, and the client's UserSsoLifetime limits how old it may be.
    /// </summary>
    public async Task<IActionResult> CompleteAsync(PageModel page, CustomerLogin login, string returnUrl, string[] methods)
    {
        var http = page.HttpContext;
        var context = await interaction.GetAuthorizationContextAsync(returnUrl, http.RequestAborted);

        await events.RaiseAsync(
            new UserLoginSuccessEvent(login.CustomerNumber, login.SubjectId.ToString(), login.DisplayName, clientId: context?.Client.ClientId),
            http.RequestAborted);

        await http.SignInAsync(new IdentityServerUser(login.SubjectId.ToString())
        {
            DisplayName = login.DisplayName,
            AuthenticationMethods = methods
        });
        PendingCustomerCode.Clear(http);
        PendingSignIn.Clear(http);

        if (context is not null)
            return context.IsNativeClient() ? page.LoadingPage(returnUrl) : new RedirectResult(returnUrl);

        // Opened directly (no client sent the customer here): the IDP has no home page.
        return page.RedirectToPage("/Customer/Done");
    }
}