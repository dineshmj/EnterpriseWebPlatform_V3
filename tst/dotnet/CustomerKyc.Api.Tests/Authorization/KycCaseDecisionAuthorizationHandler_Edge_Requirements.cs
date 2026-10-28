using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerKyc.Api.Authorization;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Tests.Authorization;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A KYC decision needs ALL of: the kyc_officer role, every permission the endpoint names
// │ (the decision AND the stage permission), the KYC department and clearance level 3 or
// │ more. Missing any one of them refuses the request.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class KycCaseDecisionAuthorizationHandler_Edge_Requirements
{
    private static readonly string[] IdentityDecision = ["kyc.case.decide", "kyc.identity.verify"];

    private static ClaimsPrincipal Officer(
        string role = "kyc_officer", string? department = "KYC", string? clearance = "3", params string[] permissions)
    {
        var claims = new List<Claim> { new("role", role) };
        claims.AddRange((permissions.Length == 0 ? IdentityDecision : permissions).Select(p => new Claim("permission", p)));
        if (department is not null)
            claims.Add(new Claim("department", department));
        if (clearance is not null)
            claims.Add(new Claim("clearance_level", clearance));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", "role"));
    }

    private static async Task<bool> IsAllowed(ClaimsPrincipal user)
    {
        var requirement = new KycCaseDecisionRequirement(IdentityDecision);
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new KycCaseDecisionAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    [Test]
    [Category("Happy")]
    public async Task Given_AKycOfficerMeetingEveryRule_When_TheyDecide_Then_ItIsAllowed()
    {
        // Given / When / Then
        (await IsAllowed(Officer())).Should().BeTrue();
        (await IsAllowed(Officer(department: "kyc"))).Should().BeTrue();
    }

    [Test]
    public async Task Given_AnotherRole_When_TheyDecide_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(role: "compliance_officer"))).Should().BeFalse();
    }

    [Test]
    public async Task Given_TheDecisionPermissionButNotTheStagePermission_When_TheyDecide_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(permissions: ["kyc.case.decide", "kyc.document.verify"]))).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("COMPLIANCE")]
    public async Task Given_NotTheKycDepartment_When_TheyDecide_Then_ItIsRefused(string? department)
    {
        // Given / When / Then
        (await IsAllowed(Officer(department: department))).Should().BeFalse();
    }

    [TestCase("2")]
    [TestCase("high")]
    [TestCase(null)]
    [Category("Boundary")]
    public async Task Given_ClearanceBelowThreeOrUnreadable_When_TheyDecide_Then_ItIsRefused(string? clearance)
    {
        // Given / When / Then
        (await IsAllowed(Officer(clearance: clearance))).Should().BeFalse();
    }
}