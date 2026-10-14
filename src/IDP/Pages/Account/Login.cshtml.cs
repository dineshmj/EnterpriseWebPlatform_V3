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

// ┌─ :: SECURITY :: Sign-in: password, then the second factor ─────────────────────────────────────
// │ Why : The sign-in page is the front door: it must not reveal which accounts exist, must survive
// │       password spraying, and must not sign anybody in on the password alone while MFA is on.
// │ How : Credentials are checked in constant work (unknown users hash a dummy password);
// │       per-account lockout plus a per-IP throttle; with MFA on the password only creates a
// │       pending sign-in and the code (or enrolment) follows; acr_values=mfa from a signed-in
// │       person asks for the code only (step-up).
// │ Ref : Features 2.1.3, 2.1.5 | OWASP A07:2021 Identification and Authentication Failures |
// │       ASVS V2.2, V2.8 | RFC 9470 | Threat-Model R-4, R-13
// └────────────────────────────────────────────────────────────────────────────────────────────────
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

        // Step-up (RFC 9470 style): a client asks for a FRESH authenticator code ("acr_values=mfa",
        // with prompt=login) for a person who is already signed in here - typically before a risky
        // action whose last MFA sign-in is too old. The password is not asked again; only the code
        // is, and a new sign-in (new auth_time, amr mfa) is issued. Anyone else signs in fully.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If a client asks for acr_values=mfa and the person is already signed in here (and
        // │ enrolled), skip the password and ask ONLY for a fresh code (step-up); anyone else
        // │ signs in fully.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (_mfaOptions.Enabled &&
            context?.AcrValues.Contains(StepUpAcr) == true &&
            User.Identity?.IsAuthenticated == true &&
            User.FindFirst("sub")?.Value is { } subjectId)
        {
            var user = await _userRepository.FindBySubjectIdAsync(subjectId, HttpContext.RequestAborted);
            if (user is { IsActive: true } && await _mfa.IsEnrolledAsync(user.Id, HttpContext.RequestAborted))
            {
                new PendingSignIn(
                    subjectId,
                    RememberLogin: false,
                    Input.ReturnUrl,
                    _time.GetUtcNow().Add(PendingSignIn.Lifetime),
                    StepUp: true)
                    .Save(HttpContext, _dataProtection);

                return RedirectToPage("/Account/Mfa");
            }
        }

        return Page();
    }

    /// <summary>The "acr_values" a client sends to ask for a fresh authenticator code.</summary>
    public const string StepUpAcr = "mfa";

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
                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ If MFA is off (Development only), the password signs the person in; otherwise it only
                    // │ creates a pending sign-in - the code (or enrolment) must follow.
                    // └──────────────────────────────────────────────────────────────────────────────────────
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