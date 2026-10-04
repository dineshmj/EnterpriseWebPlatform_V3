using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer.Events;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Services;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Account;

/// <summary>
/// Logout.
///
/// GET signs out immediately ONLY when IdentityServer says no prompt is needed,
/// i.e. a registered client initiated the logout with a valid id_token_hint.
/// Any other GET shows a confirmation page whose form is POSTed with an
/// anti-forgery token, so a third-party page cannot log the user out (CSRF).
/// </summary>
[SecurityHeaders]
[AllowAnonymous]
public sealed class LogoutModel
    : PageModel
{
    private readonly IIdentityServerInteractionService _interaction;
    private readonly IEventService _events;

    [BindProperty]
    public InputModel Input { get; set; } = default!;

    public class InputModel
    {
        public string? LogoutId { get; set; }
    }

    public LogoutModel(IIdentityServerInteractionService interaction, IEventService events)
    {
        _interaction = interaction;
        _events = events;
    }

    public async Task<IActionResult> OnGet(string? logoutId)
    {
        Input = new InputModel { LogoutId = logoutId };

        if (User.Identity?.IsAuthenticated != true)
        {
            // Nothing to sign out of; still show the logged-out page.
            return await SignOutAndContinueAsync();
        }

        var context = await _interaction.GetLogoutContextAsync(logoutId, HttpContext.RequestAborted);

        if (context?.ShowSignoutPrompt == false)
        {
            return await SignOutAndContinueAsync();
        }

        return Page();
    }

    // Razor Pages validate the anti-forgery token on POST automatically.
    public Task<IActionResult> OnPost() => SignOutAndContinueAsync();

    private async Task<IActionResult> SignOutAndContinueAsync()
    {
        var cancellationToken = HttpContext.RequestAborted;

        if (User.Identity?.IsAuthenticated == true)
        {
            // Capture the session's client list before the session is removed,
            // so the front-channel logout iframes can be rendered afterwards.
            Input.LogoutId ??= await _interaction.CreateLogoutContextAsync(cancellationToken);

            await HttpContext.SignOutAsync();

            await _events.RaiseAsync(
                new UserLogoutSuccessEvent(User.GetSubjectId(), User.GetDisplayName()),
                cancellationToken);
        }

        return RedirectToPage("/Account/LoggedOut", new { logoutId = Input.LogoutId });
    }
}