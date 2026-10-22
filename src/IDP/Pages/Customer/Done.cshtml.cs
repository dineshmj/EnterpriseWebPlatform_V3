using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Customer;

/// <summary>
/// The end of a customer's sign-in or registration when no client sent them here (the IDP's
/// pages were opened directly): the IDP has no home page of its own, so it says where to go.
/// Shows nothing about the customer - it is reachable without signing in.
/// </summary>
[SecurityHeaders]
[AllowAnonymous]
public sealed class DoneModel : PageModel
{
    public void OnGet()
    {
    }
}