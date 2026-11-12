using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Controllers;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The erasure relayed by Accounts' subscriber is refused before anything is touched
// │ when it names no usable customer or no causing message (the CausationId that makes a
// │ re-delivery idempotent). The handler is built with no dependencies at all: a refusal
// │ that still reached it would fail with a NullReferenceException.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class InternalPersonalDataErasuresController_Edge_RefusedBeforeTheHandler
{
    private static InternalPersonalDataErasuresController Controller() =>
        new(new ErasePersonalDataCommandHandler(null!, null!, TimeProvider.System, null!))
        {
            ControllerContext = new ControllerContext
            {
                // ValidationProblem needs MVC's problem-details factory.
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddControllers().Services.BuildServiceProvider()
                }
            }
        };

    private static void ShouldBeValidationProblem(IActionResult? result)
    {
        var problem = result.Should().BeAssignableTo<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ValidationProblemDetails>();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Given_NoCustomerNumber_When_TheErasureArrives_Then_ItIsRefusedAsInvalid(string? customerNumber)
    {
        // When
        var result = await Controller().Erase(new ErasePersonalDataRequest(customerNumber, Guid.NewGuid(), null, null), CancellationToken.None);

        // Then
        ShouldBeValidationProblem(result.Result);
    }

    [Test]
    public async Task Given_ACustomerNumberOverTheLimit_When_TheErasureArrives_Then_ItIsRefusedAsInvalid()
    {
        // When
        var result = await Controller().Erase(new ErasePersonalDataRequest(new string('9', 101), Guid.NewGuid(), null, null), CancellationToken.None);

        // Then
        ShouldBeValidationProblem(result.Result);
    }

    [Test]
    public async Task Given_NoCausingMessage_When_TheErasureArrives_Then_ItIsRefusedAsInvalid()
    {
        // When
        var result = await Controller().Erase(new ErasePersonalDataRequest("CUS-000123", Guid.Empty, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // Then
        ShouldBeValidationProblem(result.Result);
    }
}