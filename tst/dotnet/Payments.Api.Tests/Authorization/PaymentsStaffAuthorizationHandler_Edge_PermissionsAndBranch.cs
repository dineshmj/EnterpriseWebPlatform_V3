using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;

using AwesomeAssertions;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Payments.Api.Authorization;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Authorization;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Staff need one of the endpoint's permissions AND a branch. The audit-read permission
// │ counts only when the call comes through the Audit Journey API (act claim) - the same
// │ person calling Payments directly gets nothing from it. All-branch reads are limited to
// │ the monitoring and audit permissions.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PaymentsStaffAuthorizationHandler_Edge_PermissionsAndBranch
{
    /// <summary>The Audit Journey API's client ID, as the Payments API's ClientIds configuration holds it.</summary>
    private const string AuditJourneyApi = "Audit.JourneyApi.ClientID";

    private static readonly ClientIds ClientIds = ClientIds.From(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [$"{ClientIds.SectionName}:{nameof(ClientIds.AuditJourneyApi)}"] = AuditJourneyApi })
        .Build());

    private static ClaimsPrincipal Person(string? branch, string? actingClient, params string[] permissions)
    {
        var claims = permissions.Select(p => new Claim("permission", p)).ToList();
        if (branch is not null)
            claims.Add(new Claim("branch", branch));
        if (actingClient is not null)
            claims.Add(new Claim("act", $$"""{"client_id":"{{actingClient}}"}"""));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<bool> IsAllowed(ClaimsPrincipal user, params string[] anyOfPermissions)
    {
        var requirement = new PaymentsStaffRequirement(anyOfPermissions);
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new PaymentsStaffAuthorizationHandler(ClientIds).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ThePermissionAndABranch_When_TheEndpointIsCalled_Then_ItIsAllowed()
    {
        // Given
        var emily = Person("SYD001", null, "payment.approve");

        // When / Then
        (await IsAllowed(emily, "payment.approve", "payment.view")).Should().BeTrue();
    }

    [Test]
    public async Task Given_ThePermissionButNoBranch_When_TheEndpointIsCalled_Then_ItIsRefused()
    {
        // Given
        var nobody = Person(null, null, "payment.approve");

        // When / Then
        (await IsAllowed(nobody, "payment.approve")).Should().BeFalse();
    }

    [Test]
    public async Task Given_AnotherPermission_When_TheEndpointIsCalled_Then_ItIsRefused()
    {
        // Given
        var sophie = Person("SYD001", null, "payment.initiate");

        // When / Then
        (await IsAllowed(sophie, "payment.approve")).Should().BeFalse();
    }

    [Test]
    public void Given_TheAuditReadPermissionCalledDirectly_When_PermissionsAreWorkedOut_Then_ItDoesNotCount()
    {
        // Given
        var sarah = Person("SYD001", null, PaymentsStaffAuthorizationHandler.AuditReadPermission);

        // When
        var permissions = PaymentsStaffAuthorizationHandler.EffectivePermissionsOf(sarah, AuditJourneyApi);

        // Then
        permissions.Should().BeEmpty();
        PaymentsStaffAuthorizationHandler.ReadScopeOf(sarah, AuditJourneyApi).Should().Be((true, BranchCode.Create("SYD001")));
    }

    [Test]
    public void Given_TheAuditReadPermissionThroughTheAuditJourneyApi_When_PermissionsAreWorkedOut_Then_ItCountsForAllBranches()
    {
        // Given
        var sarah = Person(null, AuditJourneyApi, PaymentsStaffAuthorizationHandler.AuditReadPermission);

        // When
        var permissions = PaymentsStaffAuthorizationHandler.EffectivePermissionsOf(sarah, AuditJourneyApi);

        // Then
        permissions.Should().Contain(PaymentsStaffAuthorizationHandler.AuditReadPermission);
        PaymentsStaffAuthorizationHandler.ReadScopeOf(sarah, AuditJourneyApi).Should().Be((true, (BranchCode?)null));
    }

    [Test]
    public void Given_AMalformedActClaim_When_TheActingClientIsRead_Then_ThereIsNone()
    {
        // Given
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("act", "{not json")], "test"));

        // When / Then
        PaymentsStaffAuthorizationHandler.ActingClientOf(user).Should().BeNull();
    }

    [Test]
    public void Given_NoBranchAndNoAllBranchPermission_When_TheReadScopeIsWorkedOut_Then_NothingMayBeRead()
    {
        // Given
        var nobody = Person(null, null, "payment.view");

        // When / Then
        PaymentsStaffAuthorizationHandler.ReadScopeOf(nobody, AuditJourneyApi).Allowed.Should().BeFalse();
    }
}