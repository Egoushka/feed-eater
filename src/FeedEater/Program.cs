using FeedEater;
using FeedEater.Api;
using FeedEater.Eval;
using FeedEater.Mcp;
using FeedEater.Setup;
using FeedEater.Sources;
using FeedEater.Storage;
using FeedEater.Swipe;
using FeedEater.Ui;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Formatting.Compact;

if (args.FirstOrDefault() == "eval")
{
    return await EvalCommand.RunAsync(args[1..]);
}

if (args.FirstOrDefault() == "taste")
{
    return await TasteCommand.RunAsync(args[1..]);
}

if (args.FirstOrDefault() == "doctor")
{
    return await DoctorCommand.RunAsync(args[1..]);
}

if (args.FirstOrDefault() == "import-instagram")
{
    return await FeedEater.Instagram.InspectCommand.RunAsync(args[1..], Console.Out, Console.Error);
}

if (args.FirstOrDefault() == "import-opml")
{
    return await ImportOpmlCommand.RunAsync(args[1..]);
}

if (args.FirstOrDefault() == "token")
{
    return await ApiTokenCommand.RunAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddFeedEater(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1", o =>
{
    // The document is public: list the API only, not /ui, /swipe or /healthz.
    o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
    o.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] });
        return Task.CompletedTask;
    });
});
builder.Services.AddSingleton<ApiRateLimiter>();
builder.Services.AddTransient<ApiAuth>();
builder.Services.AddTransient<ApiErrors>();

var app = builder.Build();
var feedEater = app.Services.GetRequiredService<IOptions<FeedEaterOptions>>().Value;
if (feedEater.Source.KindProblem is { } kindProblem)
{
    throw new InvalidOperationException(kindProblem);
}

app.Services.GetRequiredService<DatabaseMigrator>().Run();

var token = app.Configuration["Mcp:Token"];
if (string.IsNullOrEmpty(token))
{
    app.Logger.LogWarning("Mcp:Token is not set; /mcp refuses every request");
}

if (feedEater.RunJobs && feedEater.Telegram.Token.Length == 0)
{
    app.Logger.LogWarning("FeedEater:Telegram:Token is not set; the digest and the Telegram poller are off");
}

if (feedEater.Telegram.Token.Length > 0 && feedEater.Telegram.AllowedUserId == 0)
{
    app.Logger.LogWarning("FeedEater:Telegram:AllowedUserId is 0: the bot only answers /start, with your Telegram id; set it and restart");
}

app.MapGet("/healthz", async (FeedDb db, CancellationToken ct) => await db.PingAsync(ct) ? Results.Ok() : Results.StatusCode(503));
app.MapFeedEaterMcp(token);
app.MapFeedEaterUi();
app.MapFeedEaterApi();
if (feedEater.Swipe.Enabled)
{
    app.MapFeedEaterSwipe();
}

app.Run();
return 0;

public partial class Program;
