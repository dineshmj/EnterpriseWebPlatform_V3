using Duende.IdentityServer.Models;

namespace EnterpriseWebPlatform.IdentityServer.Security;

/// <summary>
/// Whom a client serves: staff (every BSS client, the default) or EWP's customers ("My EWP"). The
/// IDP keeps the two populations apart by it: sign-in page, user table and token claims.
/// </summary>
public static class ClientAudience
{
    private const string PropertyName = "ewp:audience";
    private const string Customers = "customers";

    /// <summary>Marks a client as one of the customers' channel.</summary>
    public static Client ForCustomers(this Client client)
    {
        client.Properties[PropertyName] = Customers;
        return client;
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Staff is the default: a client must be marked explicitly to serve customers, so a
    // │ forgotten marker can only refuse a customer - never let one into a staff screen.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static bool ServesCustomers(this IConnectedApplication? application) =>
        application is Client client &&
        client.Properties.TryGetValue(PropertyName, out var audience) &&
        audience == Customers;
}