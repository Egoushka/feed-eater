using System.Net;

namespace FeedEater.Ui;

public static class Html
{
    /// <summary>Encodes text for element content and for quoted attribute values.</summary>
    public static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>The URL when it is an absolute http or https one, otherwise null: feed links are untrusted and a javascript: href would run in this origin.</summary>
    public static string? SafeUrl(string? url) =>
        url is { Length: > 0 and <= 2000 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    /// <summary>A link to another site, or plain text when the URL is not safe to link.</summary>
    public static string External(string? url, string text)
    {
        var safe = SafeUrl(url);
        return safe is null ? E(text) : $"<a href=\"{E(safe)}\" rel=\"noopener noreferrer\" target=\"_blank\">{E(text)}</a>";
    }
}
