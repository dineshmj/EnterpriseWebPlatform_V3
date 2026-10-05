using System.Security.Claims;

using Duende.IdentityModel;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Repositories;

namespace EnterpriseWebPlatform.IdentityServer.Services;

public sealed class CustomProfileService : IProfileService
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<CustomProfileService> _logger;

    public CustomProfileService(
        IUserRepository userRepository,
        ILogger<CustomProfileService> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task GetProfileDataAsync(
        ProfileDataRequestContext context,
        CancellationToken cancellationToken)
    {
        var subjectId = context.Subject.GetSubjectId();

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            _logger.LogWarning(
                "Profile data requested without a valid subject ID.");

            return;
        }

        var user = await _userRepository.FindBySubjectIdAsync(subjectId, cancellationToken);

        if (user is null)
        {
            _logger.LogWarning(
                "User with subject ID {SubjectId} was not found.",
                subjectId);

            return;
        }

        if (!user.IsActive)
        {
            _logger.LogWarning(
				"Profile data requested for inactive user with subject ID {SubjectId}.",
				subjectId);

            return;
        }

        var requestedClaimTypes = context.RequestedClaimTypes;

        // ------------------------------------------------------------
        // Identity claims
        // ------------------------------------------------------------

        if (ShouldIssueClaim(requestedClaimTypes, JwtClaimTypes.Subject))
        {
            context.IssuedClaims.Add(
                new Claim(
                    JwtClaimTypes.Subject,
                    user.SubjectId.ToString()));
        }

        if (ShouldIssueClaim(requestedClaimTypes, JwtClaimTypes.Name))
        {
            context.IssuedClaims.Add(
                new Claim(
                    JwtClaimTypes.Name,
                    $"{user.FirstName} {user.LastName}".Trim()));
        }

        if (ShouldIssueClaim(requestedClaimTypes, JwtClaimTypes.GivenName))
        {
            context.IssuedClaims.Add(
                new Claim(
                    JwtClaimTypes.GivenName,
                    user.FirstName));
        }

        if (ShouldIssueClaim(requestedClaimTypes, JwtClaimTypes.FamilyName))
        {
            context.IssuedClaims.Add(
                new Claim(
                    JwtClaimTypes.FamilyName,
                    user.LastName));
        }

        if (ShouldIssueClaim(requestedClaimTypes, JwtClaimTypes.Email))
        {
            context.IssuedClaims.Add(
                new Claim(
                    JwtClaimTypes.Email,
                    user.Email));
        }

        if (ShouldIssueClaim(
                requestedClaimTypes,
                JwtClaimTypes.PreferredUserName))
        {
            context.IssuedClaims.Add(
                new Claim(
                    JwtClaimTypes.PreferredUserName,
                    user.UserName));
        }

        // ------------------------------------------------------------
        // RBAC - Roles
        // ------------------------------------------------------------

        var roles = await _userRepository.GetRolesByUserIdAsync(user.Id, cancellationToken);

        if (requestedClaimTypes.Contains(JwtClaimTypes.Role))
        {
            foreach (var role in roles)
            {
                context.IssuedClaims.Add(
                    new Claim(
                        JwtClaimTypes.Role,
                        role));
            }
        }

        // ------------------------------------------------------------
        // RBAC - Permissions
        // ------------------------------------------------------------

        var permissions =
            await _userRepository.GetPermissionsByUserIdAsync(user.Id, cancellationToken);

        if (requestedClaimTypes.Contains("permission"))
        {
            foreach (var permission in permissions)
            {
                context.IssuedClaims.Add(
                    new Claim(
                        "permission",
                        permission));
            }
        }

        // ------------------------------------------------------------
        // ABAC - Employment / organisational attributes
        //
        // These are obtained from the User entity and its
        // employment profile where available.
        // ------------------------------------------------------------

        var employmentProfile = user.EmploymentProfile;

        if (employmentProfile is not null)
        {
            AddClaimIfRequested(
                context,
                "employee_id",
                employmentProfile.EmployeeId);

            // The staff member's LAN ID: what the APIs record next to the subject ID
            // ("decided by etpar") and what the screens show.
            AddClaimIfRequested(
                context,
                "lan_id",
                employmentProfile.LanId);

            AddClaimIfRequested(
                context,
                "employment_type",
                employmentProfile.EmploymentType);

            AddClaimIfRequested(
                context,
                "clearance_level",
                employmentProfile.ClearanceLevel.ToString());

            if (employmentProfile.Department is not null)
            {
                AddClaimIfRequested(
                    context,
                    "department",
                    employmentProfile.Department.Code);

                AddClaimIfRequested(
                    context,
                    "department_name",
                    employmentProfile.Department.Name);
            }

            if (employmentProfile.Branch is not null)
            {
                AddClaimIfRequested(
                    context,
                    "branch",
                    employmentProfile.Branch.Code);

                AddClaimIfRequested(
                    context,
                    "branch_name",
                    employmentProfile.Branch.Name);

                AddClaimIfRequested(
                    context,
                    "region",
                    employmentProfile.Branch.Region);

                AddClaimIfRequested(
                    context,
                    "branch_city",
                    employmentProfile.Branch.City);

                AddClaimIfRequested(
                    context,
                    "branch_country_code",
                    employmentProfile.Branch.CountryCode);
            }
        }

        // ReBAC relationships (an agent "manages" a customer, an officer is
        // "assigned_to" a KYC case) are NOT issued as claims: they are owned and
        // checked by the bounded context that owns the resource, at request time,
        // so they are never stale. See doc/Authorization-Model.md §6.

        _logger.LogDebug(
            "Issued profile claims for user {UserName} with subject ID {SubjectId}.",
            user.UserName,
            user.SubjectId);
    }

    public async Task IsActiveAsync(
        IsActiveContext context,
        CancellationToken cancellationToken)
    {
        var subjectId = context.Subject.GetSubjectId();

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            context.IsActive = false;
            return;
        }

        var user =
            await _userRepository.FindActiveBySubjectIdAsync(subjectId, cancellationToken);

        context.IsActive = user is not null;
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static bool ShouldIssueClaim(
        IEnumerable<string?> requestedClaimTypes,
        string claimType)
    {
        return requestedClaimTypes.Contains(claimType);
    }

    private static void AddClaimIfRequested(
        ProfileDataRequestContext context,
        string claimType,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!context.RequestedClaimTypes.Contains(claimType))
        {
            return;
        }

        context.IssuedClaims.Add(
            new Claim(
                claimType,
                value));
    }
}