using Duende.IdentityServer.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Home;

public sealed class ErrorModel : PageModel
{
    private readonly IIdentityServerInteractionService _interaction;

    public ErrorModel(
        IIdentityServerInteractionService interaction)
    {
        _interaction = interaction;
    }

    public string? Error { get; private set; }

    public string? ErrorDescription { get; private set; }

    public string? ClientId { get; private set; }

    public string? RedirectUri { get; private set; }

    public string? RequestId { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? errorId)
    {
        if (string.IsNullOrWhiteSpace(errorId))
        {
            return NotFound();
        }

        var errorContext =
            await _interaction.GetErrorContextAsync(errorId,
            HttpContext.RequestAborted);

        if (errorContext is null)
        {
            return NotFound();
        }

        Error = errorContext.Error;
        ErrorDescription = errorContext.ErrorDescription;
        ClientId = errorContext.ClientId;
        RedirectUri = errorContext.RedirectUri;
        RequestId = errorContext.RequestId;

        return Page();
    }
}