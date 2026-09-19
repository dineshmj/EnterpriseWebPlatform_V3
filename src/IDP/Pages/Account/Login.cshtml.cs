using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer;
using Duende.IdentityServer.Events;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;

using EnterpriseWebPlatform.IdentityServer.Repositories;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Account;

[SecurityHeaders]
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly IIdentityServerInteractionService _interaction;
    private readonly IEventService _events;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IUserRepository _userRepository;

    public LoginModel(
        IIdentityServerInteractionService interaction,
        IAuthenticationSchemeProvider schemeProvider,
        IUserRepository userRepository,
        IEventService events,
        TestUserStore? users = null)
    {
        _interaction = interaction;
        _schemeProvider = schemeProvider;
        _userRepository = userRepository;
        _events = events;
    }

    [BindProperty]
    public InputModel Input { get; set; } = default!;

    public class InputModel
    {
        public string Username { get; set; } = default!;

        public string Password { get; set; } = default!;

        public bool RememberLogin { get; set; }

        public string ReturnUrl { get; set; } = default!;

        public string Button { get; set; } = default!;
    }

    public IActionResult OnGet(string? returnUrl)
    {
        Input = new InputModel
        {
            ReturnUrl = returnUrl ?? "~/"
        };

        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var cancellationToken = HttpContext.RequestAborted;

        var context = await _interaction.GetAuthorizationContextAsync(
            Input.ReturnUrl,
            cancellationToken);

        if (Input.Button != "login")
        {
            if (context != null)
            {
                await _interaction.DenyAuthorizationAsync(
                    context,
                    InteractionError.AccessDenied,
                    cancellationToken);

                if (context.IsNativeClient())
                {
                    return this.LoadingPage(Input.ReturnUrl);
                }

                return Redirect(Input.ReturnUrl ?? "~/");
            }

            return Redirect("~/");
        }

        if (ModelState.IsValid)
        {
            var credentialsValid =
                await _userRepository.ValidateCredentialsAsync(
                    Input.Username,
                    Input.Password,
                    cancellationToken);

            if (credentialsValid)
            {
                var user = await _userRepository.FindByUsernameAsync(
                    Input.Username,
                    cancellationToken);

                if (user is not null)
                {
                    await _events.RaiseAsync(
                        new UserLoginSuccessEvent(
                            user.UserName,
                            user.SubjectId.ToString(),
                            $"{user.FirstName} {user.LastName}"),
                        cancellationToken);

                    // This event notifies IdentityServer's event pipeline that
                    // a login succeeded, enabling auditing, diagnostics,
                    // monitoring hooks, and security log tracking.

                    AuthenticationProperties? props = null;

                    if (Input.RememberLogin)
                    {
                        props = new AuthenticationProperties
                        {
                            IsPersistent = true,
                            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
                        };
                    }

                    var isuser = new IdentityServerUser(
                        user.SubjectId.ToString())
                    {
                        DisplayName =
                            $"{user.FirstName} {user.LastName}"
                    };

                    await HttpContext.SignInAsync(
                        isuser,
                        props);

                    // This issues the authentication cookie for the user,
                    // creating their local login session inside IdentityServer.
                    // Subsequent authorization requests use this cookie to
                    // identify the authenticated user.

                    if (context != null)
                    {
                        if (context.IsNativeClient())
                        {
                            return this.LoadingPage(Input.ReturnUrl);
                        }

                        return Redirect(Input.ReturnUrl ?? "~/");
                    }

                    if (Url.IsLocalUrl(Input.ReturnUrl))
                    {
                        return Redirect(Input.ReturnUrl);
                    }

                    if (string.IsNullOrEmpty(Input.ReturnUrl))
                    {
                        return Redirect("~/");
                    }

                    throw new ArgumentException(
                        "Invalid return URL.");
                }
            }

            await _events.RaiseAsync(
                new UserLoginFailureEvent(
                    Input.Username,
                    "invalid credentials"),
                cancellationToken);

            ModelState.AddModelError(
                string.Empty,
                "Invalid username or password");
        }

        return Page();
    }
}