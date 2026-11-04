using System.Net.Http.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Controllers;

[Authorize]
[ApiController]
[Route("bff/api/customers")]
public sealed class CustomerController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The search term is a customer number (in part), an exact e-mail address or an exact
    // │ surname: the API matches the last two through its blind index, never "contains".
    // │ The term is URL-escaped (it cannot add query parameters of its own) and capped at an
    // │ e-mail address's maximum length; the API still applies the branch scope.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private const int MaxSearchLength = 254;

    [HttpGet]
    public async Task<IActionResult> GetCustomers(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (search?.Length > MaxSearchLength)
        {
            return BadRequest();
        }

        var searchParameter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : $"&search={Uri.EscapeDataString(search.Trim())}";

        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.GetAsync(
            $"/v1/customers?pageNumber={pageNumber}&pageSize={pageSize}{searchParameter}",
            cancellationToken);

        return await ForwardJsonAsync(response, cancellationToken);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetCustomer(
        long id,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.GetAsync(
            $"/v1/customers/{id}",
            cancellationToken);

        return await ForwardJsonAsync(response, cancellationToken);
    }

    private static async Task<IActionResult> ForwardJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = "application/json",
            Content = body
        };
    }
}