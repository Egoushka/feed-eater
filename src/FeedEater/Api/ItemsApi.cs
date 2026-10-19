using FeedEater.Llm;
using FeedEater.Search;
using FeedEater.Sources;
using FeedEater.Storage;
using FeedEater.Ui;

namespace FeedEater.Api;

public static class ItemsApi
{
    private const int MaxQuestion = 500;
    private const int MaxQuery = 300;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    /// <summary>Item detail, questions about an item or the archive, and archive search, as data.</summary>
    public static RouteGroupBuilder MapItemsApi(this RouteGroupBuilder group)
    {
        group.MapGet("/items/{id:long}", async (long id, ItemStore items, ItemText text, CancellationToken ct) =>
            {
                if (await items.GetAsync(id, ct) is not { } item)
                {
                    return NotFound();
                }

                var full = await text.FullTextAsync(id, ct);
                return Results.Ok(new ItemResponse(
                    item.Id, item.Title, Html.SafeUrl(item.Url), item.Feed, Utc(item.PublishedAt), full, full is not null && full.Length > item.Content.Length,
                    item.Summary, item.Why, item.Suggestion, (short?)item.Vote, item.Saved, item.Project, item.Kind));
            })
            .WithName("GetItem")
            .Produces<ItemResponse>();

        group.MapPost("/items/{id:long}/ask", async (long id, AskRequest body, ItemAnswer answers, CancellationToken ct) =>
            {
                if (Question(body) is not { } question)
                {
                    return BadQuestion();
                }

                return await ModelCallAsync(async () =>
                    await answers.AskAsync(id, question, ct) is { } answer ? Results.Ok(new ItemAskResponse(answer)) : NotFound());
            })
            .WithName("AskItem")
            .Produces<ItemAskResponse>();

        group.MapPost("/ask", async (AskRequest body, ArchiveAnswer answers, CancellationToken ct) =>
            {
                if (Question(body) is not { } question)
                {
                    return BadQuestion();
                }

                return await ModelCallAsync(async () =>
                {
                    var result = await answers.AnswerAsync(question, ct);
                    return Results.Ok(new AskResponse(result.Answer, result.Citations.Select(c => new AskCitation(c.Number, Hit(c.Hit))).ToList()));
                });
            })
            .WithName("AskArchive")
            .Produces<AskResponse>();

        group.MapGet("/search", async (string? q, string? kind, string? project, DateTimeOffset? from, DateTimeOffset? to, bool? saved, int? limit, ArchiveSearch search, CancellationToken ct) =>
            {
                var query = q?.Trim();
                if (string.IsNullOrEmpty(query) || query.Length > MaxQuery)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"q must be 1 to {MaxQuery} characters");
                }

                var hits = await search.SearchAsync(
                    query, project, kind, from, to, saved, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct);
                return Results.Ok(hits.Select(Hit).ToList());
            })
            .WithName("SearchArchive")
            .Produces<IReadOnlyList<ApiHit>>();

        return group;
    }

    private static string? Question(AskRequest body) =>
        body.Question?.Trim() is { Length: > 0 and <= MaxQuestion } question ? question : null;

    private static IResult BadQuestion() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"question must be 1 to {MaxQuestion} characters");

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such item");

    private static ApiHit Hit(SearchHit h) =>
        new(h.Id, h.Title, Html.SafeUrl(h.Url), h.Feed, Utc(h.PublishedAt), h.Summary, h.Project, h.Kind, (short?)h.Vote, h.Saved);

    private static DateTimeOffset Utc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static async Task<IResult> ModelCallAsync(Func<Task<IResult>> call)
    {
        try
        {
            return await call();
        }
        catch (BudgetExceededException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The model budget is used up");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The model is unreachable");
        }
    }
}
