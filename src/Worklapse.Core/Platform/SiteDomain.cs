using System.Globalization;
using System.Text.RegularExpressions;

namespace Worklapse.Core.Platform;

/// <summary>
/// The site a browser shows, from the text in its address bar: the host name only (no scheme, port, path or
/// query), lower case, internationalized names in their readable (Unicode) form, without a leading "www.".
/// Only web pages have one: browser pages (about:, chrome://, edge://), files, extensions and text that isn't
/// an address (a search being typed) have none.
/// </summary>
public static partial class SiteDomain
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true };

    public static string? FromAddressBar(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (text.Any(char.IsWhiteSpace))
            return null; // a search, not an address

        // Browsers leave out "https://" (Chrome, Firefox) or show it (Edge).
        if (SchemeWithSlashes().Match(text) is { Success: true } withSlashes)
        {
            var scheme = withSlashes.Groups[1].Value.ToLowerInvariant();
            if (scheme is not ("http" or "https"))
                return null;
        }
        else if (OtherScheme().IsMatch(text))
        {
            return null; // about:blank, view-source:…, mailto:…; "localhost:8080" is a host and port instead
        }
        else
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return null;

        string host;
        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                host = uri.Host;
                break;
            case UriHostNameType.Dns:
                try
                {
                    host = Idn.GetUnicode(uri.IdnHost.TrimEnd('.')).ToLowerInvariant();
                }
                catch (ArgumentException)
                {
                    return null; // not a valid name
                }
                break;
            default:
                return null;
        }

        if (host.StartsWith("www.", StringComparison.Ordinal) && host.IndexOf('.', 4) > 0)
            host = host[4..];
        return host;
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*)://")]
    private static partial Regex SchemeWithSlashes();

    /// <summary>A scheme without "//" (about:, view-source:, mailto:), but not a host followed by a port number.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:(?!\d)")]
    private static partial Regex OtherScheme();
}
