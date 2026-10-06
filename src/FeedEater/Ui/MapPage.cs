using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Ranking;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

/// <summary><c>/ui/map</c>: the taste map. <c>?month=yyyy-MM</c> shows the votes up to the end of that month; anything else is the current month.</summary>
public sealed class MapHandler(TasteMap map, UiSession session, IOptions<FeedEaterOptions> options)
{
    public async Task<IResult> GetAsync(HttpContext ctx, CancellationToken ct)
    {
        var settings = options.Value;
        var page = new PageContext(session.AntiForgery(ctx.Request.Cookies[UiSession.CookieName] ?? ""), settings.Zone, null,
            ButtonStyle.From(settings), File.Exists(settings.ProfilePath) ? null : settings.ProfilePath);
        return UiHandlers.Html(UiPages.Map(page, await map.GetAsync(ctx.Request.Query["month"], ct)));
    }
}

public static partial class UiPages
{
    private const double MapSize = 640;
    private const double MapPad = 24;

    public static string Map(PageContext p, MapView view)
    {
        var h = new StringBuilder("<h1>Taste map</h1>");
        if (view.Dots.Count < Projection.MinPoints)
        {
            h.Append("<p class=\"empty\">The map needs at least 3 items with embeddings. There is nothing to draw yet.</p>");
            return Layout(p, "Taste map", "/ui/map", h.ToString());
        }

        h.Append($"<p class=\"meta\">Votes up to the end of {E(TasteMap.Text(view.Month))}. Items that sit close together are about similar things; the axes have no names.</p>");
        h.Append("<div class=\"tabs\">");
        foreach (var month in view.Months)
        {
            var href = month == view.CurrentMonth ? "/ui/map" : "/ui/map?month=" + TasteMap.Text(month);
            h.Append($"<a href=\"{href}\"{(month == view.Month ? " aria-current=\"page\"" : "")}>{E(TasteMap.Text(month))}</a>");
        }

        h.Append("</div>");
        MapSvg(h, view);
        h.Append("<p class=\"meta\">Green circle: 👍. Red cross: 👎. Small grey dot: no vote. A bigger mark is a heavier vote. Dashed square: items sit there, but no digest ever showed one of them.</p>");

        h.Append("<h2>Summary</h2><div class=\"scroll\"><table><thead><tr><th>Group</th><th class=\"num\">👍</th><th class=\"num\">👎</th><th class=\"num\">No vote</th></tr></thead><tbody>");
        h.Append($"<tr><td>All points</td><td class=\"num\">{N(view.Up)}</td><td class=\"num\">{N(view.Down)}</td><td class=\"num\">{N(view.Unvoted)}</td></tr>");
        foreach (var t in view.Profiles)
        {
            h.Append($"<tr><td><span class=\"badge\">{E(t.Key)}</span></td><td class=\"num\">{N(t.Up)}</td><td class=\"num\">{N(t.Down)}</td><td class=\"num\">{N(t.Unvoted)}</td></tr>");
        }

        h.Append("</tbody></table></div><p class=\"meta\">Each point counts toward the profile it is nearest to.</p>");
        return Layout(p, "Taste map", "/ui/map", h.ToString());
    }

    /// <summary>One <c>&lt;svg&gt;</c> with presentation attributes only; colours and stroke widths come from classes in app.css, so the page fits the CSP.</summary>
    private static void MapSvg(StringBuilder h, MapView view)
    {
        var cell = (MapSize - 2 * MapPad) / TasteMap.Grid;
        var alt = $"Taste map: {view.Up} liked items as green circles, {view.Down} disliked items as red crosses and {view.Unvoted} items without a vote as grey dots. The table below gives the counts.";
        h.Append($"<svg class=\"map\" viewBox=\"0 0 {Coord(MapSize)} {Coord(MapSize)}\" role=\"img\" aria-label=\"{E(alt)}\">");
        foreach (var c in view.Unseen)
        {
            h.Append($"<rect class=\"unseen\" x=\"{Coord(MapPad + c.Column * cell)}\" y=\"{Coord(MapPad + c.Row * cell)}\" width=\"{Coord(cell)}\" height=\"{Coord(cell)}\" fill=\"none\"/>");
        }

        foreach (var d in view.Dots.OrderBy(d => d.Vote != 0))
        {
            var x = MapPad + d.X * (MapSize - 2 * MapPad);
            var y = MapPad + d.Y * (MapSize - 2 * MapPad);
            var title = $"<title>{E(d.Title)}</title>";
            var shape = d.Vote switch
            {
                > 0 => $"<circle class=\"up\" cx=\"{Coord(x)}\" cy=\"{Coord(y)}\" r=\"{Coord(3 + 2 * d.Weight)}\">{title}</circle>",
                < 0 => Cross(x, y, 3 + 2 * d.Weight, title),
                _ => $"<circle class=\"none\" cx=\"{Coord(x)}\" cy=\"{Coord(y)}\" r=\"2\">{title}</circle>",
            };
            h.Append($"<a href=\"/ui/item/{N(d.Id)}\">{shape}</a>");
        }

        foreach (var l in view.Labels)
        {
            h.Append($"<text x=\"{Coord(MapPad + l.X * (MapSize - 2 * MapPad))}\" y=\"{Coord(MapPad + l.Y * (MapSize - 2 * MapPad))}\" text-anchor=\"middle\">{E(l.Key)}</text>");
        }

        h.Append("</svg>");
    }

    private static string Cross(double x, double y, double r, string title) =>
        $"<path class=\"down\" fill=\"none\" d=\"M{Coord(x - r)} {Coord(y - r)}L{Coord(x + r)} {Coord(y + r)}M{Coord(x - r)} {Coord(y + r)}L{Coord(x + r)} {Coord(y - r)}\">{title}</path>";

    private static string Coord(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
}
