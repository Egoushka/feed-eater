using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Setup;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

/// <summary><c>/ui/setup</c>: what <c>doctor</c> prints, as a read-only table. The checks run on every load.</summary>
public sealed class SetupHandler(CheckRunner runner, UiSession session, IOptions<FeedEaterOptions> options)
{
    public async Task<IResult> GetAsync(HttpContext ctx, CancellationToken ct)
    {
        var settings = options.Value;
        var page = new PageContext(session.AntiForgery(ctx.Request.Cookies[UiSession.CookieName] ?? ""), settings.Zone, null,
            ButtonStyle.From(settings), File.Exists(settings.ProfilePath) ? null : settings.ProfilePath);
        return UiHandlers.Html(UiPages.Setup(page, await runner.RunAsync(ct)));
    }
}

public static partial class UiPages
{
    public static string Setup(PageContext p, IReadOnlyList<CheckRow> rows)
    {
        var failing = rows.Count(r => r.Required && r.Result.Status == CheckStatus.Fail);
        var h = new StringBuilder("<h1>Setup</h1>");
        h.Append(failing == 0
            ? "<p class=\"notice good\" role=\"status\">Everything required works.</p>"
            : $"<p class=\"notice bad\" role=\"alert\">{failing.ToString(CultureInfo.InvariantCulture)} required {(failing == 1 ? "check fails" : "checks fail")}.</p>");
        h.Append("<div class=\"scroll\"><table><thead><tr><th>Status</th><th>Check</th><th>What was found</th><th>Fix</th></tr></thead><tbody>");
        foreach (var row in rows)
        {
            var r = row.Result;
            var style = r.Status switch { CheckStatus.Ok => " good", CheckStatus.Warn => " warn", CheckStatus.Fail => " bad", _ => "" };
            h.Append($"<tr><td><span class=\"badge{style}\">{E(DoctorReport.Label(r.Status))}</span></td><td>{E(row.Name)}{(row.Required ? "" : " <span class=\"muted\">(optional)</span>")}</td>")
                .Append($"<td>{E(r.Detail)}</td><td>{E(r.Fix ?? "")}</td></tr>");
        }

        h.Append("</tbody></table></div>");
        h.Append("<p class=\"muted\">The same checks as <code>docker compose run --rm feed-eater doctor</code>, run again each time this page loads.</p>");
        return Layout(p, "Setup", "/ui/setup", h.ToString());
    }
}
