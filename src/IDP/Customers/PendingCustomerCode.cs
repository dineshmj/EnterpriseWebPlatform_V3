using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace EnterpriseWebPlatform.IdentityServer.Customers;

/// <summary>
/// The browser's side of a one-time code in flight: which code row (not the code), and where to
/// go afterwards. Five minutes, encrypted, HttpOnly, __Host-. Tries are counted on the row, so a
/// copied or replayed cookie gains nothing. DevelopmentCode: the code itself, ONLY while
/// CustomerSignIn:ShowCodesOnPage is on (Development), in place of the SMS.
/// </summary>
public sealed record PendingCustomerCode(
    Guid CodeId,
    Data.Entities.CustomerCodePurpose Purpose,
    string ReturnUrl,
    DateTimeOffset ExpiresAt,
    string? DevelopmentCode = null)
{
    private const string CookieName = "__Host-ewp.customer.code";
    private const string ProtectorPurpose = "EnterpriseWebPlatform.IdentityServer.Customers.PendingCode.v1";

    public void Save(HttpContext http, IDataProtectionProvider dataProtection) =>
        http.Response.Cookies.Append(CookieName,
            dataProtection.CreateProtector(ProtectorPurpose).Protect(JsonSerializer.Serialize(this)),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = CustomerOneTimeCodes.Lifetime });

    public static PendingCustomerCode? Load(HttpContext http, IDataProtectionProvider dataProtection, TimeProvider time)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var sealedValue))
            return null;
        try
        {
            var pending = JsonSerializer.Deserialize<PendingCustomerCode>(dataProtection.CreateProtector(ProtectorPurpose).Unprotect(sealedValue));
            return pending is not null && pending.ExpiresAt > time.GetUtcNow() ? pending : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear(HttpContext http) => http.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, HttpOnly = true, Path = "/" });
}