using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Cards;
using FeedEater.Digest;
using FeedEater.Trials;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

/// <summary><c>/ui/cards</c>: the adoption cards by state, a card with its evidence, and the buttons that move it.</summary>
public sealed class CardsHandler(CardStore cards, TrialStore trials, TrialQueue queue, UiSession session, IOptions<FeedEaterOptions> options)
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
            ? UiHandlers.Html(UiPages.CardDetail(Page(ctx), card, await trials.ListAsync(id, null, 20, ct), options.Value.Trials.Enabled))
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

    public async Task<IResult> QueueTrialAsync(long id, HttpContext ctx, CancellationToken ct)
    {
        var result = await queue.QueueAsync(id, ct);
        return result.Outcome switch
        {
            QueueOutcome.Queued or QueueOutcome.AlreadyActive => UiHandlers.SeeOther(ctx, $"/ui/cards/{id.ToString(CultureInfo.InvariantCulture)}"),
            QueueOutcome.NoSuchCard => UiHandlers.Html(UiPages.Message(Page(ctx), "Not found", "There is no such card."), StatusCodes.Status404NotFound),
            _ => UiHandlers.Html(UiPages.Message(Page(ctx), "Not queued", result.Message), StatusCodes.Status409Conflict),
        };
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

    public static string CardDetail(PageContext p, CardRow c, IReadOnlyList<TrialRow> trials, bool trialsOn)
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
        Trials(h, p, c, trials, trialsOn);
        return Layout(p, c.Title, "/ui/cards", h.ToString());
    }

    private static string TrialClass(string status) => status switch { "passed" => "good", "failed" or "blocked" => "bad", "running" or "queued" => "warn", _ => "" };

    private static void Trials(StringBuilder h, PageContext p, CardRow c, IReadOnlyList<TrialRow> trials, bool trialsOn)
    {
        h.Append("<h2>Trials</h2>");
        var active = trials.Any(t => t.Status is "queued" or "running");
        if (c.State == "trial" && c.StateChangedAt is not null && trialsOn && !active)
        {
            h.Append($"<form method=\"post\" action=\"/ui/cards/{N(c.Id)}/trial\" class=\"stack\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(p.Csrf)}\"><div class=\"actions\"><button type=\"submit\">Queue trial</button></div></form>");
        }
        else if (c.State == "trial" && !trialsOn)
        {
            h.Append("<p class=\"meta\">Trials are off. Set Trials:Enabled to run this card.</p>");
        }

        if (trials.Count == 0)
        {
            h.Append("<p class=\"empty\">No trial has run for this card.</p>");
            return;
        }

        foreach (var t in trials)
        {
            h.Append($"<section class=\"trial\"><p class=\"meta\"><span class=\"badge {TrialClass(t.Status)}\">{E(t.Status)}</span> <code>{E(t.Branch)}</code> · <time datetime=\"{t.RequestedAt.ToString("O", CultureInfo.InvariantCulture)}\">{E(Local(p, t.RequestedAt))}</time></p>");
            if (t.DiffStat.Length > 0)
            {
                h.Append($"<p>Diff: {E(t.DiffStat)}</p>");
            }

            if (t.Benchmark.Length > 0)
            {
                h.Append($"<p>Measured: {E(t.Benchmark)}</p>");
            }

            if (t.Tests.Length > 0)
            {
                h.Append($"<h3>Tests</h3><pre class=\"content\">{E(t.Tests)}</pre>");
            }

            if (t.ResultMd.Length > 0)
            {
                h.Append($"<pre class=\"content\">{E(t.ResultMd)}</pre>");
            }

            h.Append("</section>");
        }
    }
}
