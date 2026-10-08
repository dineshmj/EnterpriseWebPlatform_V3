using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

using Duende.IdentityServer;
using Duende.IdentityServer.Events;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;

using EnterpriseWebPlatform.IdentityServer.Repositories;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Account;

[SecurityHeaders]
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel : PageModel
{
    private readonly IIdentityServerInteractionService _interaction;
    private readonly IEventService _events;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IUserRepository _userRepository;
    private readonly MfaService _mfa;
    private readonly MfaOptions _mfaOptions;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly TimeProvider _time;

    public LoginModel(
        IIdentityServerInteractionService interaction,
        IAuthenticationSchemeProvider schemeProvider,
        IUserRepository userRepository,
        IEventService events,
        MfaService mfa,
        IOptions<MfaOptions> mfaOptions,
        IDataProtectionProvider dataProtection,
        TimeProvider time,
        TestUserStore? users = null)
    {
        _interaction = interaction;
        _schemeProvider = schemeProvider;
        _userRepository = userRepository;
        _events = events;
        _mfa = mfa;
        _mfaOptions = mfaOptions.Value;
        _dataProtection = dataProtection;
        _time = time;
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

    /// <summary>
    /// Display name of the client application that sent the user here
    /// ("Sign in to continue to ..."); null when signing in directly.
    /// </summary>
    public string? ClientName { get; private set; }

    public async Task<IActionResult> OnGet(string? returnUrl)
    {
        Input = new InputModel
        {
            ReturnUrl = returnUrl ?? "~/"
        };

        var context = await _interaction.GetAuthorizationContextAsync(
            Input.ReturnUrl,
            HttpContext.RequestAborted);

        ClientName = DisplayNameOf(context);

        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var cancellationToken = HttpContext.RequestAborted;

        var context = await _interaction.GetAuthorizationContextAsync(
            Input.ReturnUrl,
            cancellationToken);

        ClientName = DisplayNameOf(context);

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
                    // MFA off: signed in now, as before. MFA on: not yet - the password was only the
                    // first factor; the authenticator code (or, the first time, enrolment) follows.
                    if (!_mfaOptions.Enabled)
                    {
                        return await SignInCompletion.CompleteAsync(
                            this, _interaction, _events, user, Input.RememberLogin, Input.ReturnUrl, withMfa: false);
                    }

                    new PendingSignIn(
                        user.SubjectId.ToString(),
                        Input.RememberLogin,
                        Input.ReturnUrl,
                        _time.GetUtcNow().Add(PendingSignIn.Lifetime))
                        .Save(HttpContext, _dataProtection);

                    return await _mfa.IsEnrolledAsync(user.Id, cancellationToken)
                        ? RedirectToPage("/Account/Mfa")
                        : RedirectToPage("/Account/MfaSetup");
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

    private static string? DisplayNameOf(AuthorizationRequest? context)
        => context?.Client is { } client
            ? (string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientId : client.ClientName)
            : null;
}