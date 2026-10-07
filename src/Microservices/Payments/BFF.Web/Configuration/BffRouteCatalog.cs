using System.Text.RegularExpressions;

namespace EnterpriseWebPlatform.Payments.Bff.Web.Configuration;

public static partial class BffRouteCatalog
{
    // The pages seeded in EwpBssShellDb.sql (New Payment, View Payments, Payment Approvals, Payment
    // Processing Monitor), plus the payment
    // page a 401 returns the user to and a notification opens ("?paymentId=<number>",
    // nothing else). Keeping the list here prevents the silent-login endpoint from becoming
    // an open redirector.
    public const string NewPayment = "/v1/payments/new";
    public const string Payments = "/v1/payments/view-all";
    public const string PaymentDetails = "/v1/payments/view-details";
    public const string Approvals = "/v1/payments/approvals/view-all";
    public const string Processing = "/v1/payments/processing/view-all";

    /// <summary>True for an allowed MFE path, with or without the trailing slash of the static export.</summary>
    public static bool IsAllowedSpaRoute(string? returnUrl)
    {
        if (returnUrl is null) return false;
        if (returnUrl.TrimEnd('/') is NewPayment or Payments or PaymentDetails or Approvals or Processing) return true;

        var match = PathWithId().Match(returnUrl);
        return match.Success && match.Groups["path"].Value == PaymentDetails && match.Groups["name"].Value == "paymentId";
    }

    [GeneratedRegex(@"^(?<path>/[a-z0-9/-]+?)/?\?(?<name>[A-Za-z]+)=\d{1,18}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathWithId();
}