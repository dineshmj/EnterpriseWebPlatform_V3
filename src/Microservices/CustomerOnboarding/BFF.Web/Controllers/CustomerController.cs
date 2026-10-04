using System.Net.Http.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Controllers;

[Authorize]
[ApiController]
[Route("bff/api/customers")]
public sealed class CustomerController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCustomers(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.GetAsync(
            $"/v1/customers?pageNumber={pageNumber}&pageSize={pageSize}",
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