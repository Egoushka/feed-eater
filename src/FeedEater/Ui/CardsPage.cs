using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Cards;
using FeedEater.Digest;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

/// <summary><c>/ui/cards</c>: the adoption cards by state, a card with its evidence, and the buttons that move it.</summary>
public sealed class CardsHandler(CardStore cards, UiSession session, IOptions<FeedEaterOptions> options)
{
    private const int ListLimit = 200;

    private PageContext Page(HttpContext ctx)
    {
        var o = options.Value;
        return new PageContext(session.AntiForgery(ctx.Request.Cookies[UiSession.CookieName] ?? ""), o.Zone, null, ButtonStyle.From(o), null);
    }

    public async Task<IResult> ListAsync(HttpContext ctx, CancellationToken ct)
    {
        var state = ctx.Request.Query["state"].ToString();
        var filter = CardRules.States.Contains(state, StringComparer.Ordinal) ? state : null;
        return UiHandlers.Html(UiPages.Cards(Page(ctx), filter, await cards.ListAsync(filter, ListLimit, ct), await cards.CountsAsync(ct), options.Value.Cards.Enabled));
    }

    public async Task<IResult> DetailAsync(long id, HttpContext ctx, CancellationToken ct) =>
        await cards.GetAsync(id, ct) is { } card
            ? UiHandlers.Html(UiPages.CardDetail(Page(ctx), card))
            : UiHandlers.Html(UiPages.Message(Page(ctx), "Not found", "There is no such card."), StatusCodes.Status404NotFound);

    public async Task<IResult> SetStateAsync(long id, HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        if (CardRules.CheckTransition(form["state"].ToString(), form["reason"]) is not null)
        {
            return Results.BadRequest();
        }

        return await cards.SetStateAsync(id, form["state"].ToString(), form["reason"], ct)
            ? UiHandlers.SeeOther(ctx, $"/ui/cards/{id.ToString(CultureInfo.InvariantCulture)}")
            : UiHandlers.Html(UiPages.Message(Page(ctx), "Not found", "There is no such card."), StatusCodes.Status404NotFound);
    }
}

public static partial class UiPages
{
    private static string StateClass(string state) => state switch { "adopt" => "good", "trial" => "warn", "dropped" => "bad", _ => "" };

    public static string Cards(PageContext p, string? state, IReadOnlyList<CardRow> rows, IReadOnlyDictionary<string, int> counts, bool jobOn)
    {
        var h = new StringBuilder("<h1>Cards</h1>");
        h.Append("<nav class=\"tabs\" aria-label=\"State\">");
        h.Append($"<a href=\"/ui/cards\"{(state is null ? " aria-current=\"page\"" : "")}>All <span class=\"count\">{N(counts.Values.Sum())}</span></a>");
        foreach (var s in CardRules.States)
        {
            h.Append($"<a href=\"/ui/cards?state={s}\"{(s == state ? " aria-current=\"page\"" : "")}>{E(s)} <span class=\"count\">{N(counts.GetValueOrDefault(s))}</span></a>");
        }

        h.Append("</nav>");
        if (rows.Count == 0)
        {
            h.Append(jobOn
                ? "<p class=\"empty\">No cards here yet. The job reads your repos and writes cards once a day.</p>"
                : "<p class=\"empty\">No cards here. The cards job is off: set Cards:Enabled and Cards:Root.</p>");
            return Layout(p, "Cards", "/ui/cards", h.ToString());
        }

        h.Append("<ol class=\"cards\">");
        foreach (var c in rows)
        {
            h.Append($"<li class=\"card\"><h2><a href=\"/ui/cards/{N(c.Id)}\">{E(c.Title)}</a></h2>")
                .Append($"<p class=\"meta\"><span class=\"badge {StateClass(c.State)}\">{E(c.State)}</span> {E(c.Repo)} · {N(c.Evidence.Count)} evidence · <time datetime=\"{c.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}\">{E(Local(p, c.CreatedAt))}</time></p>")
                .Append($"<p>{E(c.WhereInStack)}</p>")
                .Append($"<p class=\"why\">{E(c.Gain)}</p></li>");
        }

        h.Append("</ol>");
        return Layout(p, "Cards", "/ui/cards", h.ToString());
    }

    public static string CardDetail(PageContext p, CardRow c)
    {
        var h = new StringBuilder($"<p class=\"meta\"><a href=\"/ui/cards\">Cards</a></p><h1>{E(c.Title)}</h1>");
        h.Append($"<p class=\"meta\"><span class=\"badge {StateClass(c.State)}\">{E(c.State)}</span> {E(c.Repo)} · <time datetime=\"{c.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}\">{E(Local(p, c.CreatedAt))}</time></p>");
        if (c.StateReason.Length > 0)
        {
            h.Append($"<p class=\"suggestion\">{E(c.StateReason)}{(c.StateChangedAt is { } at ? $" <span class=\"muted\">· {E(Local(p, at))}</span>" : "")}</p>");
        }

        h.Append($"<h2>What</h2><p>{E(c.What)}</p>")
            .Append($"<h2>Where in your stack</h2><p>{E(c.WhereInStack)}</p>")
            .Append("<h2>Evidence</h2>")
            .Append(c.EvidenceChecked ? "<p class=\"meta\">The linked pages were read.</p>" : "<p class=\"meta\">Titles only: the linked pages were not read.</p>")
            .Append("<ul>");
        foreach (var e in c.Evidence)
        {
            h.Append($"<li>{External(e.Url, e.Title)} <span class=\"muted\">· {E(e.Source)}{(e.At is { } when ? " · " + E(Local(p, when)) : "")}</span></li>");
        }

        h.Append("</ul>")
            .Append($"<h2>Effort</h2><p>{E(c.Effort)}</p><h2>Gain</h2><p>{E(c.Gain)}</p>")
            .Append("<h2>Decide</h2>")
            .Append($"<form method=\"post\" action=\"/ui/cards/{N(c.Id)}/state\" class=\"stack\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(p.Csrf)}\">")
            .Append("<label for=\"reason\">Reason</label><input id=\"reason\" name=\"reason\" maxlength=\"500\" required autocomplete=\"off\">")
            .Append("<div class=\"actions\">");
        foreach (var (s, label) in new[] { ("assess", "Assess"), ("trial", "Run trial"), ("adopt", "Adopt"), ("hold", "Hold"), ("dropped", "Drop") })
        {
            h.Append($"<button type=\"submit\" name=\"state\" value=\"{s}\"{(s == c.State ? " aria-pressed=\"true\"" : "")}>{label}</button>");
        }

        h.Append("</div></form>");
        return Layout(p, c.Title, "/ui/cards", h.ToString());
    }
}
