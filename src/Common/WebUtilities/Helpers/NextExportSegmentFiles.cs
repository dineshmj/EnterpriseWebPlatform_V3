using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseWebPlatform.Common.WebUtilities.Helpers;

// ┌─ :: PERFORMANCE :: Next.js 16 static export: segment prefetch files ──────────────────────────
// │ Why : The exported pages prefetch their data as "__next.v1.customers.view-all.__PAGE__.txt"
// │       (the segment path with "/" as "."), but the export writes it in folders:
// │       "__next.v1/customers/view-all/__PAGE__.txt". Every prefetch answered 404 - a console
// │       error, and the data loaded again on navigation.
// │ How : Before the static files: a request for a "__next.<a>.<b>...txt" file that does not
// │       exist is rewritten to "__next.<a>/<b>/...txt" - only when that file exists in wwwroot,
// │       only segments of letters, digits, "_" and "-" (no "..", nothing outside wwwroot).
// │       Remove once Next.js exports the file under the name its client asks for.
// │ Ref : Next.js segment-cache convertSegmentPathToStaticExportFilename | open item "Next.js 16
// │       prefetch 404"
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static partial class NextExportSegmentFiles
{
    /// <summary>Call before <c>UseStaticFiles</c>.</summary>
    public static IApplicationBuilder UseEwpNextExportSegmentFiles(this IApplicationBuilder app)
    {
        var webRoot = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;

        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            if (path is not null && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                var slash = path.LastIndexOf('/');
                var match = DottedSegmentFile().Match(path[(slash + 1)..]);
                if (match.Success && !webRoot.GetFileInfo(path).Exists)
                {
                    var segments = match.Groups["segments"].Value.Split('.');
                    var nested = $"{path[..(slash + 1)]}__next.{segments[0]}/{string.Join('/', segments[1..])}.txt";
                    if (webRoot.GetFileInfo(nested).Exists)
                        context.Request.Path = nested;
                }
            }

            await next();
        });
    }

    // "__next." + at least two dot-separated segments + ".txt"; each segment [A-Za-z0-9_-]+.
    [GeneratedRegex(@"^__next\.(?<segments>[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+)\.txt$")]
    private static partial Regex DottedSegmentFile();
}