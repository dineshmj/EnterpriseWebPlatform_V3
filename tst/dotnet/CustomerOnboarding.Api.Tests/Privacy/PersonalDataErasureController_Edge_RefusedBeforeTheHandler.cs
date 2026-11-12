using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using AwesomeAssertions;
using Moq;

using EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.PersonalDataErasure;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Erasure cannot be undone, so its endpoints refuse what they cannot trust BEFORE any
// │ handler runs: no acting person (no sub), no usable customer number, a reason that is
// │ not one of the two codes, remarks over the limit. Every handler dependency here is a
// │ strict mock - a refusal that still reached a repository fails the test. The policy
// │ endpoint reports the retention in force against the law's, for the screen's warning.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PersonalDataErasureController_Edge_RefusedBeforeTheHandler
{
    private static readonly PersonalDataErasurePolicy NoRetention = new(TimeSpan.Zero, TimeSpan.FromDays(7));

    private static PersonalDataErasureController Controller(PersonalDataErasurePolicy policy, string? sub)
    {
        var commands = new PersonalDataErasureCommandHandler(
            Mock.Of<ICustomerRepository>(MockBehavior.Strict),
            Mock.Of<IPersonalDataErasureRequestRepository>(MockBehavior.Strict),
            Mock.Of<IApplicationUnitOfWork>(MockBehavior.Strict),
            policy,
            TimeProvider.System);
        var queries = new GetPersonalDataErasureRequestsQueryHandler(
            Mock.Of<IPersonalDataErasureReadContext>(MockBehavior.Strict),
            Mock.Of<IStaffDirectory>(MockBehavior.Strict),
            TimeProvider.System);

        var claims = sub is null ? [] : new[] { new Claim("sub", sub) };
        return new PersonalDataErasureController(commands, queries, policy)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                    // ValidationProblem needs MVC's problem-details factory.
                    RequestServices = new ServiceCollection().AddControllers().Services.BuildServiceProvider()
                }
            }
        };
    }

    private static void ShouldBeValidationProblem(IActionResult? result)
    {
        var problem = result.Should().BeAssignableTo<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ValidationProblemDetails>();
    }

    [Test]
    public async Task Given_NoActingPerson_When_TheRequestsAreListed_Then_ItIsForbidden()
    {
        // Given
        var controller = Controller(NoRetention, sub: null);

        // When
        var result = await controller.List(CancellationToken.None);

        // Then
        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Test]
    public async Task Given_NoActingPerson_When_AnErasureIsRequested_Then_ItIsForbidden()
    {
        // Given
        var controller = Controller(NoRetention, sub: null);

        // When
        var result = await controller.RequestErasure(new RequestPersonalDataErasureRequest("CUS-000123", "CUSTOMER_REQUEST"), CancellationToken.None);

        // Then
        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Test]
    public async Task Given_NoActingPerson_When_ARequestIsApprovedOrRejected_Then_BothAreForbidden()
    {
        // Given
        var controller = Controller(NoRetention, sub: null);

        // When
        var approved = await controller.Approve(7, CancellationToken.None);
        var rejected = await controller.Reject(7, new PersonalDataErasureDecisionRequest("The customer withdrew the request."), CancellationToken.None);

        // Then
        approved.Result.Should().BeOfType<ForbidResult>();
        rejected.Result.Should().BeOfType<ForbidResult>();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("CUS-0001234567890123456789012345")]   // 32 characters: over the limit
    public async Task Given_NoUsableCustomerNumber_When_AnErasureIsRequested_Then_ItIsRefusedAsInvalid(string? customerNumber)
    {
        // Given
        var controller = Controller(NoRetention, sub: "sub-daniel");

        // When
        var result = await controller.RequestErasure(new RequestPersonalDataErasureRequest(customerNumber, "CUSTOMER_REQUEST"), CancellationToken.None);

        // Then
        ShouldBeValidationProblem(result.Result);
    }

    [TestCase(null)]
    [TestCase("customer_request")]
    [TestCase("The customer phoned and asked us to forget her.")]   // free text is never accepted
    public async Task Given_AReasonThatIsNotOneOfTheCodes_When_AnErasureIsRequested_Then_ItIsRefusedAsInvalid(string? reasonCode)
    {
        // Given
        var controller = Controller(NoRetention, sub: "sub-daniel");

        // When
        var result = await controller.RequestErasure(new RequestPersonalDataErasureRequest("CUS-000123", reasonCode), CancellationToken.None);

        // Then
        ShouldBeValidationProblem(result.Result);
    }

    [Test]
    public async Task Given_RemarksLongerThanAllowed_When_ARequestIsRejected_Then_ItIsRefusedAsInvalid()
    {
        // Given
        var controller = Controller(NoRetention, sub: "sub-michael");
        var remarks = new string('x', PersonalDataErasureRequest.MaxRemarksLength + 1);

        // When
        var result = await controller.Reject(7, new PersonalDataErasureDecisionRequest(remarks), CancellationToken.None);

        // Then
        ShouldBeValidationProblem(result.Result);
    }

    [Test]
    public void Given_NoRetentionPeriod_When_ThePolicyIsRead_Then_ItIsReportedAsShorterThanTheLaws()
    {
        // Given
        var controller = Controller(NoRetention, sub: "sub-daniel");

        // When
        var policy = controller.Policy().Result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeOfType<PersonalDataErasurePolicyResponse>().Subject;

        // Then
        policy.Should().Be(new PersonalDataErasurePolicyResponse(0, 2557, IsShorterThanLegal: true));
    }

    [Test]
    [Category("Happy")]
    public void Given_TheLegalRetentionPeriod_When_ThePolicyIsRead_Then_NoWarningIsDue()
    {
        // Given
        var controller = Controller(new PersonalDataErasurePolicy(TimeSpan.FromDays(2557), TimeSpan.FromDays(7)), sub: "sub-daniel");

        // When
        var policy = (PersonalDataErasurePolicyResponse)((OkObjectResult)controller.Policy().Result!).Value!;

        // Then
        policy.Should().Be(new PersonalDataErasurePolicyResponse(2557, 2557, IsShorterThanLegal: false));
    }
}