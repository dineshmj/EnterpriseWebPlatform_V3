using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Services;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Controllers;

[Authorize]
[ApiController]
[Route("bff/api/onboarding")]
public sealed class OnboardingController(
    IHttpClientFactory httpClientFactory,
    IM2MAccessTokenService m2mAccessTokenService,
    ILogger<OnboardingController> logger) : ControllerBase
{
    [HttpGet("applications")]
    public async Task<IActionResult> GetApplications(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.GetAsync(
            $"/v1/onboarding/applications?pageNumber={pageNumber}&pageSize={pageSize}",
            cancellationToken);

        return await ForwardJsonAsync(response, cancellationToken);
    }

    [HttpPost("applications")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAndSubmit(
        [FromForm] CreateAndSubmitOnboardingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.KycProof is null || request.KycProof.Length == 0 ||
            request.TaxProof is null || request.TaxProof.Length == 0)
        {
            return BadRequest(new { message = "Both PDF documents are required." });
        }

        if (!IsPdf(request.KycProof) || !IsPdf(request.TaxProof))
        {
            return UnprocessableEntity(new { message = "Both KYC documents must be PDF files." });
        }

        long customerId;
        string customerNumber;

        if (request.CustomerId.HasValue)
        {
            var existingCustomer = await GetCustomerAsync(
                request.CustomerId.Value,
                cancellationToken);

            if (!existingCustomer.Success)
            {
                return existingCustomer.Result!;
            }

            customerId = existingCustomer.CustomerId;
            customerNumber = existingCustomer.CustomerNumber;
        }
        else
        {
            // The authenticated user's subject identifies the actor creating the customer,
            // not the customer being created. A staff user may create many customer records,
            // so the customer's optional OIDC SubjectId must not be populated from the
            // staff user's token. It remains null until the customer's own identity is
            // explicitly linked through an appropriate identity-association workflow.
            var customer = await CreateCustomerAsync(request, cancellationToken);
            if (!customer.Success)
            {
                return customer.Result!;
            }

            customerId = customer.CustomerId!.Value;
            customerNumber = customer.CustomerNumber!;
        }

        var application = await CreateApplicationAsync(customerId, cancellationToken);
        if (!application.Success)
        {
            return application.Result!;
        }

        var uploadedDocumentIds = new List<Guid>();

        try
        {
            string m2mToken;
            try
            {
                m2mToken = await m2mAccessTokenService.GetAccessTokenAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                logger.LogError(ex, "Unable to obtain the Documents Management M2M access token.");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Documents Management authorization could not be established. The onboarding application remains available for retry."
                });
            }

            var kycDocument = await UploadDocumentAsync(
                request.KycProof,
                "KYCProof",
                customerNumber,
                m2mToken,
                cancellationToken);

            if (!kycDocument.Success)
            {
                return kycDocument.Result!;
            }

            uploadedDocumentIds.Add(kycDocument.DocumentId!.Value);

            var taxDocument = await UploadDocumentAsync(
                request.TaxProof,
                "TaxProof",
                customerNumber,
                m2mToken,
                cancellationToken);

            if (!taxDocument.Success)
            {
                await TryCompensateDocumentsAsync(uploadedDocumentIds, m2mToken, cancellationToken);
                return taxDocument.Result!;
            }

            uploadedDocumentIds.Add(taxDocument.DocumentId!.Value);

            var applicationDetails = await GetApplicationAsync(
                application.ApplicationId!.Value,
                cancellationToken);

            if (!applicationDetails.Success)
            {
                await TryCompensateDocumentsAsync(uploadedDocumentIds, m2mToken, cancellationToken);
                return applicationDetails.Result!;
            }

            var submit = await SubmitApplicationAsync(
                application.ApplicationId.Value,
                applicationDetails.Version,
                cancellationToken);

            if (!submit.Success)
            {
                await TryCompensateDocumentsAsync(uploadedDocumentIds, m2mToken, cancellationToken);
                return submit.Result!;
            }

            return StatusCode(StatusCodes.Status201Created, new
            {
                customerId,
                customerNumber,
                applicationId = application.ApplicationId,
                applicationNumber = application.ApplicationNumber,
                status = "SUBMITTED",
                documents = new[]
                {
                    new { documentType = "KYCProof", documentId = uploadedDocumentIds[0] },
                    new { documentType = "TaxProof", documentId = uploadedDocumentIds[1] }
                }
            });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Unexpected downstream communication failure during onboarding submission.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "A downstream service could not be reached. The onboarding application remains available for retry."
            });
        }
    }

    private async Task<(bool Success, long CustomerId, string CustomerNumber, IActionResult? Result)> GetCustomerAsync(
        long customerId,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.GetAsync(
            $"/v1/customers/{customerId}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (false, 0, string.Empty, await ForwardJsonAsync(response, cancellationToken));
        }

        var result = await response.Content.ReadFromJsonAsync<CustomerDetailsResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("CO API returned an empty customer response.");

        return (true, result.CustomerId, result.CustomerNumber, null);
    }

    private async Task<(bool Success, long? CustomerId, string? CustomerNumber, IActionResult? Result)> CreateCustomerAsync(
        CreateAndSubmitOnboardingRequest request,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.PostAsJsonAsync(
            "/v1/customers",
            new
            {
                firstName = request.FirstName,
                lastName = request.LastName,
                email = request.Email,
                phoneNumber = request.PhoneNumber,
                customerType = 1
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (false, null, null, await ForwardJsonAsync(response, cancellationToken));
        }

        var result = await response.Content.ReadFromJsonAsync<CreateCustomerResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("CO API returned an empty customer response.");

        return (true, result.CustomerId, result.CustomerNumber, null);
    }

    private async Task<(bool Success, long? ApplicationId, string? ApplicationNumber, IActionResult? Result)> CreateApplicationAsync(
        long? customerId,
        CancellationToken cancellationToken)
    {
        var applicationNumber = $"APP-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.PostAsJsonAsync(
            "/v1/onboarding/applications",
            new { customerId, applicationNumber },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (false, null, null, await ForwardJsonAsync(response, cancellationToken));
        }

        var result = await response.Content.ReadFromJsonAsync<CreateApplicationResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("CO API returned an empty application response.");

        return (true, result.ApplicationId, result.ApplicationNumber, null);
    }

    private async Task<(bool Success, Guid? DocumentId, IActionResult? Result)> UploadDocumentAsync(
        IFormFile file,
        string documentType,
        string businessReference,
        string m2mToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("DocumentsManagementApi");
        using var content = new MultipartFormDataContent();
        await using var stream = file.OpenReadStream();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        content.Add(fileContent, "File", file.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/documents")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", m2mToken);
        request.Headers.Add("X-Document-Type", documentType);
        request.Headers.Add("X-Business-Reference", businessReference);

        // IMPORTANT: this POST is deliberately not retried automatically. The current
        // DM API has no idempotency-key contract, so a retry could create a duplicate file.
        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (false, null, await ForwardJsonAsync(response, cancellationToken));
        }

        var result = await response.Content.ReadFromJsonAsync<UploadDocumentResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("DM API returned an empty document response.");

        return (true, result.DocumentId, null);
    }

    private async Task<(bool Success, long Version, IActionResult? Result)> GetApplicationAsync(
        long applicationId,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.GetAsync(
            $"/v1/onboarding/applications/{applicationId}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (false, 0, await ForwardJsonAsync(response, cancellationToken));
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var version = document.RootElement.GetProperty("version").GetInt64();
        return (true, version, null);
    }

    private async Task<(bool Success, IActionResult? Result)> SubmitApplicationAsync(
        long applicationId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("CustomerOnboardingApi");
        using var response = await client.PostAsJsonAsync(
            $"/v1/onboarding/applications/{applicationId}/submit",
            new { expectedVersion },
            cancellationToken);

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ForwardJsonAsync(response, cancellationToken));
    }

    private async Task TryCompensateDocumentsAsync(
        IEnumerable<Guid> documentIds,
        string m2mToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("DocumentsManagementApi");

        foreach (var documentId in documentIds)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Delete,
                    $"/v1/documents/{documentId}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", m2mToken);

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Document compensation failed for {DocumentId}. HTTP {StatusCode}.",
                        documentId,
                        (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Document compensation threw an exception for {DocumentId}.", documentId);
            }
        }
    }

    private static bool IsPdf(IFormFile file)
        => string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
           && file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

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

    public sealed class CreateAndSubmitOnboardingRequest
    {
        [Required, MaxLength(100)]
        public string FirstName { get; init; } = string.Empty;

        [Required, MaxLength(100)]
        public string LastName { get; init; } = string.Empty;

        [Required, EmailAddress, MaxLength(254)]
        public string Email { get; init; } = string.Empty;

        [Required, MaxLength(30)]
        public string PhoneNumber { get; init; } = string.Empty;

        public long? CustomerId { get; init; }

        [Required]
        public IFormFile? KycProof { get; init; }

        [Required]
        public IFormFile? TaxProof { get; init; }
    }

    private sealed record CreateCustomerResult(long CustomerId, string CustomerNumber);
    private sealed record CustomerDetailsResult(long CustomerId, string CustomerNumber);
    private sealed record CreateApplicationResult(long ApplicationId, string ApplicationNumber);
    private sealed record UploadDocumentResult(Guid DocumentId, string FileName, string ContentType, long Size, string ContentHash, DateTimeOffset CreatedAt, long Version);
}