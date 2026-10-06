using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Ui;
using static FeedEater.Ui.Html;

namespace FeedEater.Sources;

/// <summary>
/// The built-in reader's part of /ui/sources: a panel under the stats (add a feed, import and export OPML, feed health with a remove
/// button) and the POST routes behind it. Registered only in builtin mode, so Miniflux mode shows and routes none of it.
/// </summary>
public sealed class SourcesUi(FeedStore feeds, FeedManager manager, UiSession session, IOptions<FeedEaterOptions> options)
{
    private const int MaxOpmlBytes = 2 * 1024 * 1024;

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/sources/add", AddAsync);
        routes.MapPost("/sources/remove", RemoveAsync);
        routes.MapPost("/sources/import", ImportAsync);
        routes.MapGet("/sources.opml", ExportAsync);
    }

    public async Task<string> PanelAsync(string csrf, IQueryCollection query, CancellationToken ct)
    {
        var h = new StringBuilder();
        if (Status(query) is { } status)
        {
            h.Append($"<p class=\"notice good\" role=\"status\">{E(status)}</p>");
        }

        h.Append("<h2>Add a feed</h2><p class=\"meta\">A feed address, or a site address: the feed link is looked up on the page.</p>")
            .Append($"<form method=\"post\" action=\"/ui/sources/add\" class=\"stack\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(csrf)}\">")
            .Append("<div><label for=\"feed-url\">Address</label><input id=\"feed-url\" name=\"url\" type=\"text\" inputmode=\"url\" maxlength=\"2000\" required></div>")
            .Append("<div><button type=\"submit\" class=\"primary\">Add feed</button></div></form>")
            .Append("<h2>OPML</h2><p class=\"meta\">Folders in the file become categories.</p>")
            .Append($"<form method=\"post\" action=\"/ui/sources/import\" enctype=\"multipart/form-data\" class=\"stack\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(csrf)}\">")
            .Append("<div><label for=\"opml-file\">OPML file</label><input id=\"opml-file\" name=\"file\" type=\"file\" accept=\".opml,.xml,text/xml,text/x-opml\" required></div>")
            .Append("<div><button type=\"submit\">Import</button> <a href=\"/ui/sources.opml\">Export OPML</a></div></form>");

        var all = await feeds.ListAsync(ct);
        h.Append("<h2>Feeds</h2>");
        if (all.Count == 0)
        {
            h.Append("<p class=\"empty\">No feeds yet. Add one above or import an OPML file.</p>");
            return h.ToString();
        }

        h.Append("<div class=\"scroll\"><table><thead><tr><th>Feed</th><th>Address</th><th>Last fetched</th><th>Status</th><th><span class=\"visually-hidden\">Remove</span></th></tr></thead><tbody>");
        foreach (var f in all)
        {
            var status2 = f.FailCount > 0
                ? $"<span class=\"badge warn\">failing {f.FailCount.ToString(CultureInfo.InvariantCulture)}x</span> {E(f.LastError ?? "")}"
                : f.LastFetchedAt is null ? "<span class=\"muted\">not fetched yet</span>" : "<span class=\"badge good\">ok</span>";
            var fetched = f.LastFetchedAt is { } at ? E(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), options.Value.Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) : "–";
            h.Append($"<tr><td>{E(f.Title)}{(string.IsNullOrEmpty(f.Category) ? "" : $" <span class=\"muted\">{E(f.Category)}</span>")}</td>")
                .Append($"<td><code class=\"copy\">{E(f.FeedUrl)}</code></td><td>{fetched}</td><td>{status2}</td>")
                .Append($"<td><details class=\"confirm\"><summary>Remove</summary><form method=\"post\" action=\"/ui/sources/remove\" class=\"inline\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(csrf)}\">")
                .Append($"<input type=\"hidden\" name=\"feed\" value=\"{f.Id.ToString(CultureInfo.InvariantCulture)}\">")
                .Append($"<button type=\"submit\" class=\"danger\" aria-label=\"Remove {E(f.Title)}\">Remove feed</button></form></details></td></tr>");
        }

        h.Append("</tbody></table></div><p class=\"meta\">Removing a feed keeps its items in the archive, with their votes.</p>");
        return h.ToString();
    }

    /// <summary>A fixed text for the result code in the query, with only numbers taken from it.</summary>
    private static string? Status(IQueryCollection query) => query["src"].ToString() switch
    {
        "added" => "Feed added.",
        "exists" => "Already subscribed.",
        "removed" => "Feed removed.",
        "imported" => $"Imported {Count(query["n"])} feeds ({Count(query["dup"])} were already there); they are fetched within a few minutes.",
        _ => null,
    };

    private static string Count(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : "0";

    private async Task<IResult> AddAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        var result = await manager.AddAsync(form["url"].ToString(), ct);
        return !result.Ok
            ? Failure(ctx, "Feed not added", result.Error!)
            : UiHandlers.SeeOther(ctx, result.Existing ? "/ui/sources?src=exists" : "/ui/sources?src=added");
    }

    private async Task<IResult> RemoveAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        if (!long.TryParse(form["feed"], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return Results.BadRequest();
        }

        return await feeds.RemoveAsync(id, ct)
            ? UiHandlers.SeeOther(ctx, "/ui/sources?src=removed")
            : Failure(ctx, "Not found", "There is no such feed.", StatusCodes.Status404NotFound);
    }

    private async Task<IResult> ImportAsync(HttpContext ctx, CancellationToken ct)
    {
        var file = (await ctx.Request.ReadFormAsync(ct)).Files.GetFile("file");
        if (file is null || file.Length is 0 or > MaxOpmlBytes)
        {
            return Failure(ctx, "Not imported", "Choose an OPML file of up to 2 MB.");
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var result = await manager.ImportAsync(await reader.ReadToEndAsync(ct), ct);
        return !result.Ok
            ? Failure(ctx, "Not imported", result.Error!)
            : UiHandlers.SeeOther(ctx, $"/ui/sources?src=imported&n={result.Added.ToString(CultureInfo.InvariantCulture)}&dup={result.Existing.ToString(CultureInfo.InvariantCulture)}");
    }

    private async Task<IResult> ExportAsync(CancellationToken ct) =>
        Results.File(Encoding.UTF8.GetBytes(await manager.ExportAsync(ct)), "text/x-opml; charset=utf-8", "feed-eater.opml");

    /// <summary>A failure changes nothing, so it is answered in place rather than redirected: the reason can be shown without being passed in the URL.</summary>
    private IResult Failure(HttpContext ctx, string title, string text, int status = StatusCodes.Status422UnprocessableEntity)
    {
        var csrf = session.AntiForgery(ctx.Request.Cookies[UiSession.CookieName] ?? "");
        return UiHandlers.Html(UiPages.Message(new PageContext(csrf, options.Value.Zone, null), title, text), status);
    }
}
