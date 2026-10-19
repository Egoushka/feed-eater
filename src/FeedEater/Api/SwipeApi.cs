using FeedEater.Swipe;

namespace FeedEater.Api;

public static class SwipeApi
{
    /// <summary>The swipe session over the API: the same service as the /swipe page, without cookies or anti-forgery values.</summary>
    public static RouteGroupBuilder MapSwipeApi(this RouteGroupBuilder group)
    {
        var swipe = group.MapGroup("/swipe");

        swipe.MapPost("/next", async (ApiNextBody b, SwipeService svc, CancellationToken ct) =>
                b.Peek == true ? await svc.PeekAsync(Math.Max(b.Position ?? 0, 0), ct) : await svc.NextAsync(b.Position, ct))
            .WithName("SwipeNext")
            .Produces<NextResult>();
        swipe.MapPost("/act", async (ApiActBody b, SwipeService svc, CancellationToken ct) =>
                await svc.ActAsync(b.Session, b.Position, b.Act ?? "", Guid.TryParse(b.ClientId, out var id) ? id : null, ct))
            .WithName("SwipeAct")
            .Produces<ActResult>();
        swipe.MapPost("/undo", async (ApiUndoBody b, SwipeService svc, CancellationToken ct) =>
                Guid.TryParse(b.ClientId, out var id) ? await svc.UndoAsync(b.Session, b.Position, id, ct) : new ActResult(false, "Nothing to do."))
            .WithName("SwipeUndo")
            .Produces<ActResult>();
        swipe.MapPost("/event", async (ApiEventBody b, SwipeService svc, CancellationToken ct) =>
                new OkResponse(await svc.EventAsync(b.Session, b.Position, b.ShownAt, b.DwellMs, b.Direction, ct)))
            .WithName("SwipeEvent")
            .Produces<OkResponse>();
        swipe.MapPost("/felt", async (ApiFeltBody b, SwipeService svc, CancellationToken ct) =>
                new OkResponse(await svc.FeltAsync(b.Session, b.Felt, ct)))
            .WithName("SwipeFelt")
            .Produces<OkResponse>();
        swipe.MapPost("/end", async (ApiEndBody b, SwipeService svc, CancellationToken ct) =>
                await svc.EndAsync(b.Session, ct) ?? new NextResult("closed", Closed: new ClosedView(null, "empty")))
            .WithName("SwipeEnd")
            .Produces<NextResult>();

        return group;
    }
}
