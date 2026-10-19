using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QRCoder;
using FeedEater.Api;
using FeedEater.Digest;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

/// <summary>Pairs a phone from <c>/ui/setup</c>: the one place a plain API token is shown, as a QR code and as text.</summary>
public sealed class PairingHandler(ApiTokenStore tokens, UiSession session, IOptions<FeedEaterOptions> options)
{
    private const int NameLimit = 60;

    public async Task<IResult> PairAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        var name = form["name"].ToString().Trim();
        name = name.Length == 0 ? "phone" : name[..Math.Min(name.Length, NameLimit)];
        var (_, token) = await tokens.CreateAsync(name, [ApiScopes.App], ct);

        var settings = options.Value;
        var baseUrl = settings.Api.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            baseUrl = $"{(UiGuard.IsHttps(ctx.Request) ? "https" : "http")}://{ctx.Request.Host.Value}";
        }

        var page = new PageContext(session.AntiForgery(ctx.Request.Cookies[UiSession.CookieName] ?? ""), settings.Zone, null, ButtonStyle.From(settings));
        return UiHandlers.Html(UiPages.Paired(page, name, baseUrl, token));
    }

    public async Task<IResult> RevokeAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        if (long.TryParse(form["id"], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            await tokens.RevokeAsync(id, ct);
        }

        return UiHandlers.SeeOther(ctx, "/ui/setup");
    }
}

public static partial class UiPages
{
    public static string Pairing(PageContext p, IReadOnlyList<ApiTokenRow> tokens)
    {
        var h = new StringBuilder("<h2>Phone app</h2>");
        var live = tokens.Where(t => t.RevokedAt is null).ToList();
        if (live.Count == 0)
        {
            h.Append("<p class=\"muted\">No phone is paired.</p>");
        }
        else
        {
            h.Append("<div class=\"scroll\"><table><thead><tr><th>Name</th><th>Created</th><th>Last used</th><th></th></tr></thead><tbody>");
            foreach (var t in live)
            {
                h.Append($"<tr><td>{E(t.Name)}</td><td>{E(Local(p, t.CreatedAt))}</td><td>{(t.LastUsedAt is { } used ? E(Local(p, used)) : "<span class=\"muted\">never</span>")}</td>")
                    .Append($"<td><form method=\"post\" action=\"/ui/setup/revoke\" class=\"inline\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(p.Csrf)}\"><input type=\"hidden\" name=\"id\" value=\"{t.Id.ToString(CultureInfo.InvariantCulture)}\">")
                    .Append($"<button type=\"submit\" class=\"danger\" aria-label=\"Revoke {E(t.Name)}\">Revoke</button></form></td></tr>");
            }

            h.Append("</tbody></table></div>");
        }

        h.Append("<form method=\"post\" action=\"/ui/setup/pair\"><label>Name <input name=\"name\" value=\"phone\" maxlength=\"60\"></label>");
        h.Append($"<input type=\"hidden\" name=\"_csrf\" value=\"{E(p.Csrf)}\"><button type=\"submit\" class=\"primary\">Pair a phone</button></form>");
        return h.ToString();
    }

    public static string Paired(PageContext p, string name, string baseUrl, string token)
    {
        var payload = JsonSerializer.Serialize(new { baseUrl, token });
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(new Size(280, 280), Color.Black, Color.White, true, SvgQRCode.SizingMode.ViewBoxAttribute);

        var h = new StringBuilder($"<h1>Pair {E(name)}</h1>");
        h.Append("<p class=\"notice warn\" role=\"status\">Shown once — scan it now. This token is not shown again; revoke it on the setup page if it is lost. Reloading this page creates another token.</p>");
        h.Append($"<div role=\"img\" aria-label=\"QR code with the address and token\">{svg}</div>");
        h.Append($"<p>Address: <code>{E(baseUrl)}</code></p><p>Token: <code>{E(token)}</code></p>");
        h.Append("<p><a href=\"/ui/setup\">Back to setup</a></p>");
        return Layout(p, "Pair a phone", "/ui/setup", h.ToString());
    }
}
