using FeedEater.Library;
using FeedEater.Ui;

namespace FeedEater.Api;

public static class LibraryApi
{
    private const int MaxTitle = 500;
    private const int MaxText = 60_000;
    private const int MaxNote = 2_000;
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    /// <summary>The library for the phone app: save a shared link or an existing item, list, mark read, note, remove.</summary>
    public static RouteGroupBuilder MapLibraryApi(this RouteGroupBuilder group)
    {
        var library = group.MapGroup("/library");

        library.MapPost("", async (LibrarySaveBody body, LibraryService service, CancellationToken ct) =>
            {
                if (body.Url is null)
                {
                    return Problem(StatusCodes.Status400BadRequest, "url is required");
                }

                if (body.Title is { Length: > MaxTitle } || body.Text is { Length: > MaxText })
                {
                    return Problem(StatusCodes.Status400BadRequest, $"title is at most {MaxTitle} characters and text at most {MaxText}");
                }

                return Saved(await service.SaveUrlAsync(body.Url, body.Title, body.Text, "share", ct));
            })
            .WithName("SaveToLibrary")
            .Produces<LibrarySaveResponse>();

        library.MapPost("/{itemId:long}", async (long itemId, LibraryService service, CancellationToken ct) =>
                Saved(await service.SaveAsync(itemId, "api", ct)))
            .WithName("SaveItemToLibrary")
            .Produces<LibrarySaveResponse>();

        library.MapGet("", async (string? state, long? after, int? limit, LibraryStore store, CancellationToken ct) =>
            {
                if (!Enum.TryParse<LibraryState>(state ?? "unread", true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return Problem(StatusCodes.Status400BadRequest, "state is unread, read or all");
                }

                var rows = await store.ListAsync(parsed, after, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct);
                return Results.Ok(rows.Select(Entry).ToList());
            })
            .WithName("ListLibrary")
            .Produces<IReadOnlyList<LibraryEntry>>();

        library.MapPost("/{itemId:long}/read", async (long itemId, LibraryReadBody body, LibraryStore store, TimeProvider time, CancellationToken ct) =>
            {
                if (body.Read is not { } read)
                {
                    return Problem(StatusCodes.Status400BadRequest, "read is required");
                }

                return await store.MarkReadAsync(itemId, read, time.GetUtcNow(), ct) ? Results.NoContent() : NotSaved();
            })
            .WithName("MarkLibraryRead");

        library.MapPut("/{itemId:long}/note", async (long itemId, LibraryNoteBody body, LibraryStore store, CancellationToken ct) =>
            {
                if (body.Note is { Length: > MaxNote })
                {
                    return Problem(StatusCodes.Status400BadRequest, $"note is at most {MaxNote} characters");
                }

                return await store.SetNoteAsync(itemId, body.Note, ct) ? Results.NoContent() : NotSaved();
            })
            .WithName("SetLibraryNote");

        library.MapDelete("/{itemId:long}", async (long itemId, LibraryService service, CancellationToken ct) =>
                await service.UnsaveAsync(itemId, ct) ? Results.NoContent() : NotSaved())
            .WithName("RemoveFromLibrary");

        return group;
    }

    private static IResult Saved(SaveResult result) => result.Status switch
    {
        SaveStatus.Saved => Results.Json(new LibrarySaveResponse(result.ItemId!.Value, false), statusCode: StatusCodes.Status201Created),
        SaveStatus.AlreadySaved => Results.Ok(new LibrarySaveResponse(result.ItemId!.Value, true)),
        SaveStatus.NoItem => Problem(StatusCodes.Status404NotFound, "No such item"),
        SaveStatus.BadUrl => Problem(StatusCodes.Status400BadRequest, "Only http and https links can be saved"),
        var other => throw new InvalidOperationException($"Unknown save status {other}"),
    };

    private static IResult NotSaved() => Problem(StatusCodes.Status404NotFound, "That item is not in the library");

    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);

    private static LibraryEntry Entry(LibraryRow r) => new(
        r.ItemId, r.Title, Html.SafeUrl(r.Url), r.Feed, Utc(r.PublishedAt), Utc(r.SavedAt), r.ReadAt is { } read ? Utc(read) : null,
        r.Note, r.Source, r.Summary, r.FullText, r.Enriched);

    private static DateTimeOffset Utc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
