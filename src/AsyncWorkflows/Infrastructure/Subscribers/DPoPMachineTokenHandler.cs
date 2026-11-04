using System.Net.Http.Headers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

// ┌─ :: SECURITY :: A fresh DPoP proof on every API call ──────────────────────────────────────────
// │ Why : The API takes a DPoP-bound token only with a proof of its key made for exactly that
// │       request, and takes each proof once - a proof copied with the token, or reused by a
// │       retry, is refused.
// │ How : Sits INSIDE the client's resilience handler (added after it), so it runs once per
// │       attempt: each attempt carries "Authorization: DPoP <token>" and a new proof for its
// │       method and address, with the hash of the token (ath).
// │ Ref : Features 2.1.2 | RFC 9449 sections 4, 7 | FAPI 2.0 Security Profile §5.3.2.2
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Puts the worker's DPoP-bound machine token and a new proof on each request.</summary>
public sealed class DPoPMachineTokenHandler(CachedM2MTokenClient tokenClient, MachineKeys keys) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenClient.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Remove("DPoP");
        request.Headers.Add("DPoP", keys.Proof(request.Method, request.RequestUri!, token));
        return await base.SendAsync(request, cancellationToken);
    }
}