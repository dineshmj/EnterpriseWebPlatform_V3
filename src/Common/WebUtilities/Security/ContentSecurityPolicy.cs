using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

/// <summary>
/// Builds a strict Content-Security-Policy for a BFF that serves a Next.js static
/// export (OWASP A03 / XSS defence in depth).
///
/// Scripts: only the BFF's own files ('self') plus the exact inline scripts of the
/// exported pages, allowed by their SHA-256 hashes. The hashes are computed from the
/// files actually served, at startup, so they can never drift from the build; an
/// injected script (stored or reflected XSS) has a different hash and is blocked.
///
/// Styles: 'unsafe-inline' is kept, because React renders style="…" attributes,
/// which CSP cannot allow by hash. Injected CSS is a much smaller risk than script.
/// </summary>
public static partial class ContentSecurityPolicy
{
    /// <summary>SHA-256 hashes ('sha256-…') of every inline script in the HTML files under <paramref name="webRoot"/>.</summary>
    public static IReadOnlyList<string> InlineScriptHashes(string webRoot)
    {
        if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
            return [];

        var hashes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(webRoot, "*.html", SearchOption.AllDirectories))
        {
            var html = File.ReadAllText(file, Encoding.UTF8);

            foreach (Match match in InlineScript().Matches(html))
            {
                // The browser hashes the element's text exactly as written (scripts are raw text).
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(match.Groups["body"].Value));
                hashes.Add($"'sha256-{Convert.ToBase64String(digest)}'");
            }
        }

        return [.. hashes];
    }

    /// <param name="webRoot">Folder of the exported pages served by this BFF.</param>
    /// <param name="frameAncestors">Who may frame this app ("'none'" for a top-level host).</param>
    /// <param name="frameSources">What this app may frame ("'self'", MFE origins, the IDP…); empty = nothing.</param>
    public static string Build(
        string webRoot,
        IEnumerable<string> frameAncestors,
        IEnumerable<string> frameSources)
    {
        var scriptSources = string.Join(' ', new[] { "'self'" }.Concat(InlineScriptHashes(webRoot)));
        var frames = frameSources.ToList();

        return string.Join("; ",
            "default-src 'self'",
            $"script-src {scriptSources}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            "connect-src 'self'",
            $"frame-src {(frames.Count == 0 ? "'none'" : string.Join(' ', frames))}",
            $"frame-ancestors {string.Join(' ', frameAncestors)}",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'");
    }

    /// <summary>The origin (scheme://host:port) of an absolute URL.</summary>
    public static string Origin(string url) => new Uri(url).GetLeftPart(UriPartial.Authority);

    // <script ...> without a src attribute, capturing its body.
    [GeneratedRegex(@"<script(?![^>]*\bsrc\s*=)[^>]*>(?<body>[\s\S]*?)</script>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();
}
