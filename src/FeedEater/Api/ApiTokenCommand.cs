using FeedEater.Storage;

namespace FeedEater.Api;

public static class ApiTokenCommand
{
    /// <summary>Entry for <c>dotnet FeedEater.dll token create|list|revoke</c>. Applies the migrations first, like the service does.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        const string usage = "Usage: token create <name> | token list | token revoke <id>";
        long id = 0;
        var ok = args switch
        {
            ["create", _] => true,
            ["list"] => true,
            ["revoke", var raw] => long.TryParse(raw, out id),
            _ => false,
        };
        if (!ok)
        {
            await Console.Error.WriteLineAsync(usage);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        host.Services.GetRequiredService<DatabaseMigrator>().Run();
        var store = host.Services.GetRequiredService<ApiTokenStore>();

        switch (args[0])
        {
            case "create":
                var (newId, token) = await store.CreateAsync(args[1], [ApiScopes.App], CancellationToken.None);
                Console.WriteLine($"Token {newId} ({args[1]}) created. It is shown once; copy it now:");
                Console.WriteLine(token);
                return 0;
            case "list":
                foreach (var t in await store.ListAsync(CancellationToken.None))
                {
                    Console.WriteLine($"{t.Id}\t{t.Name}\t{string.Join(',', t.Scopes)}\tcreated {t.CreatedAt:u}\tlast used {(t.LastUsedAt is { } used ? $"{used:u}" : "never")}\t{(t.RevokedAt is { } revoked ? $"revoked {revoked:u}" : "active")}");
                }

                return 0;
            default:
                if (await store.RevokeAsync(id, CancellationToken.None))
                {
                    Console.WriteLine($"Token {id} revoked.");
                    return 0;
                }

                await Console.Error.WriteLineAsync($"No active token {id}.");
                return 1;
        }
    }
}
