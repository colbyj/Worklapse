using Worklapse.Core.Platform;

namespace Worklapse.Tests;

public class SiteDomainTests
{
    [Theory]
    // As Chrome and Firefox show addresses (no scheme) and as Edge does (with it).
    [InlineData("github.com/dotnet/runtime", "github.com")]
    [InlineData("https://github.com/dotnet/runtime", "github.com")]
    [InlineData("http://example.com", "example.com")]
    [InlineData("HTTPS://Docs.Microsoft.COM/en-us/", "docs.microsoft.com")]
    [InlineData("  github.com  ", "github.com")]
    // "www." goes; other subdomains stay.
    [InlineData("www.github.com/x", "github.com")]
    [InlineData("https://www.youtube.com/watch?v=1", "youtube.com")]
    [InlineData("mail.google.com/mail/u/0/#inbox", "mail.google.com")]
    [InlineData("www.com", "www.com")]
    // Ports, user info, query and fragment aren't part of the site.
    [InlineData("localhost:5173/app", "localhost")]
    [InlineData("http://localhost:8080", "localhost")]
    [InlineData("https://user:secret@example.com:8443/path?q=1#top", "example.com")]
    [InlineData("example.com.", "example.com")]
    // IP addresses.
    [InlineData("192.168.1.10:8080/admin", "192.168.1.10")]
    [InlineData("http://10.0.0.1", "10.0.0.1")]
    [InlineData("[::1]:3000/", "[::1]")]
    [InlineData("http://[2001:db8::1]/", "[2001:db8::1]")]
    // Internationalized names, whether shown readable or encoded, come out readable.
    [InlineData("bücher.de/kategorie", "bücher.de")]
    [InlineData("https://xn--bcher-kva.de/", "bücher.de")]
    [InlineData("www.例え.jp", "例え.jp")]
    [InlineData("Bücher.DE", "bücher.de")]
    public void TheSiteIsTheHostName(string addressBar, string expected) =>
        Assert.Equal(expected, SiteDomain.FromAddressBar(addressBar));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Browser pages, files and other schemes.
    [InlineData("about:blank")]
    [InlineData("about:preferences#privacy")]
    [InlineData("chrome://settings/")]
    [InlineData("edge://newtab/")]
    [InlineData("chrome-extension://abcdef/popup.html")]
    [InlineData("moz-extension://1234/page.html")]
    [InlineData("view-source:https://example.com/")]
    [InlineData("file:///C:/Users/me/report.pdf")]
    [InlineData("ftp://files.example.com/")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    // A search being typed.
    [InlineData("how to cook rice")]
    [InlineData("github dotnet runtime")]
    public void ThereIsNoSiteForPagesThatArentOnTheWeb(string? addressBar) =>
        Assert.Null(SiteDomain.FromAddressBar(addressBar));
}
