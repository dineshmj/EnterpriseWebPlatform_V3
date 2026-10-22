using System.Security.Claims;

using Duende.IdentityModel;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Repositories;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Services;

// ┌─ :: SECURITY :: Claims issued to tokens ───────────────────────────────────────────────────────
// │ Why : Authorization in every API decides from these claims; a deactivated person must stop
// │       getting tokens at once.
// │ How : Claims are re-read from the database on every issue and refresh: roles, permissions and
// │       the ABAC profile (branch, department, clearance); an inactive user gets none; the act
// │       chain is carried into access tokens.
// │ Ref : Features 2.2.2, 2.2.4 | Authorization-Model 3, 5 | ASVS V4.1 |
// │       RFC 9068 (JWT access tokens)
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class CustomProfileService : IProfileService
{
    private readonly IUserRepository _userRepository;
    private readonly CustomerLoginStore _customerLogins;
    private readonly ILogger<CustomProfileService> _logger;

    public CustomProfileService(
        IUserRepository userRepository,
        CustomerLoginStore customerLogins,
        ILogger<CustomProfileService> logger)
    {
        _userRepository = userRepository;
        _customerLogins = customerLogins;
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

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ A customer client's tokens come ONLY from customer_logins, a staff client's ONLY
        // │ from users: the client decides which population is looked up, so a customer can
        // │ never receive a staff claim (role, permission, branch) and no staff member a
        // │ customer number.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (context.Application.ServesCustomers())
        {
            await IssueCustomerClaimsAsync(context, subjectId, cancellationToken);
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

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the person has been deactivated, issue no claims - their sessions stop working at
        // │ the next token refresh.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!user.IsActive)
        {
            _logger.LogWarning(
				"Profile data requested for inactive user with subject ID {SubjectId}.",
				subjectId);

            return;
        }

        var requestedClaimTypes = context.RequestedClaimTypes;

        // ------------------------------------------------------------
        // Delegation (token exchange, RFC 8693): the chain of services acting for this
        // person travels in the access token, so a Domain API knows who is calling for whom.
        // ------------------------------------------------------------

        if (context.Caller == Duende.IdentityServer.IdentityServerConstants.ProfileDataCallers.ClaimsProviderAccessToken &&
            context.Subject.FindFirst(JwtClaimTypes.Actor) is { } actor)
        {
            context.IssuedClaims.Add(actor);
        }

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

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The same split for "may this session or token go on?": a staff member's IDP
        // │ session is NOT active for a customer client (and the reverse), so Duende asks for
        // │ a new sign-in on the right page instead of reusing the other population's session.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (context.Application.ServesCustomers())
        {
            var customer = await _customerLogins.FindBySubjectIdAsync(subjectId, cancellationToken);
            context.IsActive = customer is { IsActive: true };
            return;
        }

        var user =
            await _userRepository.FindActiveBySubjectIdAsync(subjectId, cancellationToken);

        context.IsActive = user is not null;
    }

    // A customer's claims: who they are and their customer number - nothing organisational.
    private async Task IssueCustomerClaimsAsync(ProfileDataRequestContext context, string subjectId, CancellationToken cancellationToken)
    {
        var customer = await _customerLogins.FindBySubjectIdAsync(subjectId, cancellationToken);
        if (customer is not { IsActive: true })
        {
            _logger.LogWarning("Profile data requested for unknown or inactive customer sign-in {SubjectId}.", subjectId);
            return;
        }

        if (context.Caller == Duende.IdentityServer.IdentityServerConstants.ProfileDataCallers.ClaimsProviderAccessToken &&
            context.Subject.FindFirst(JwtClaimTypes.Actor) is { } actor)
        {
            context.IssuedClaims.Add(actor);
        }

        AddClaimIfRequested(context, JwtClaimTypes.Name, customer.DisplayName);
        AddClaimIfRequested(context, CustomerNumberClaim, customer.CustomerNumber);
    }

    /// <summary>The claim that names the customer in a customer token (EWP's customer number).</summary>
    public const string CustomerNumberClaim = "customer_number";

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