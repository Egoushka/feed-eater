using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PriorArt.Bench;

// recall  --gold F [--systems b0,b0seed,b1,warm] --mcp-url URL [--audits DIR] [--seed-files a.md,b.md] [--b1 F] [--guard lenient|strict] [--limit 10] --out F
// latency --gold F --mcp-url URL [--queries 200]
// The MCP token is read from FEED_MCP_TOKEN.
var options = ParseOptions(args.Skip(1).ToArray());
if (args.Length == 0 || args[0] is not ("recall" or "latency"))
{
    Console.Error.WriteLine("usage: recall --gold F --mcp-url URL [--systems b0,b0seed,b1,warm] [--audits DIR] [--seed-files a.md,b.md] [--b1 F] [--guard lenient|strict] [--limit 10] --out F\n       latency --gold F --mcp-url URL [--queries 200]");
    return 2;
}

var token = Environment.GetEnvironmentVariable("FEED_MCP_TOKEN") ?? "";
if (token.Length == 0 || !options.TryGetValue("gold", out var goldPath) || !options.TryGetValue("mcp-url", out var url))
{
    Console.Error.WriteLine("FEED_MCP_TOKEN, --gold and --mcp-url are required.");
    return 2;
}

var decisions = Decision.Load(goldPath);
using var mcp = new Mcp(url, token);
return args[0] == "recall" ? await RecallAsync() : await LatencyAsync();

async Task<int> RecallAsync()
{
    var systems = Option("systems", "b0,b0seed,b1,warm").Split(',');
    var limit = int.Parse(Option("limit", "10"), CultureInfo.InvariantCulture);
    var guard = Option("guard", "lenient");
    var audits = Option("audits", "");
    var seedFiles = Option("seed-files", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var b1 = options.TryGetValue("b1", out var b1Path) ? JsonDocument.Parse(File.ReadAllText(b1Path)).RootElement.Clone() : (JsonElement?)null;
    var report = new Dictionary<string, object>();
    var macro = new Dictionary<string, double>();
    foreach (var system in systems)
    {
        var rows = new List<object>();
        int found = 0, total = 0;
        double sum = 0;
        foreach (var d in decisions)
        {
            var results = system switch
            {
                "warm" => await Systems.WarmAsync(mcp, d, limit, CancellationToken.None),
                "b0" => await Systems.B0Async(mcp, d, audits, null, limit, CancellationToken.None),
                "b0seed" => await Systems.B0Async(mcp, d, audits, seedFiles, limit, CancellationToken.None),
                "b1" when b1 is { } file => Systems.B1(file, d, limit),
                _ => throw new InvalidOperationException($"system {system} needs its input (b1: --b1 FILE) or is unknown."),
            };
            var kept = guard == "strict" ? results.Where(r => r.Date is not { } day || day <= d.Date).ToList() : results.ToList();
            var hit = d.Found(kept.Select(r => r.Text));
            found += hit.Count;
            total += d.Gold.Count;
            sum += (double)hit.Count / d.Gold.Count;
            rows.Add(new { d.Id, d.Title, results = results.Count, kept = kept.Count, found = hit, missed = d.Gold.Except(hit).ToList() });
        }

        var (lo, hi) = Wilson(found, total);
        macro[system] = sum / decisions.Count;
        report[system] = new { recall_at_10 = Math.Round(macro[system], 4), micro = Math.Round((double)found / total, 4), wilson95 = new[] { Math.Round(lo, 4), Math.Round(hi, 4) }, found, total, per_decision = rows };
        Console.WriteLine($"{system,-7} recall@{limit} = {macro[system]:0.000} (micro {found}/{total} = {(double)found / total:0.000}, Wilson 95% {lo:0.000} to {hi:0.000})");
    }

    var failures = new List<string>();
    if (!macro.TryGetValue("warm", out var warm))
    {
        failures.Add("the warm system was not run");
    }
    else
    {
        if (warm < 0.60)
        {
            failures.Add($"D-warm recall@10 {warm:0.000} < 0.60");
        }

        if (!macro.TryGetValue("b0", out var b0) || warm < b0 + 0.15)
        {
            failures.Add($"D-warm {warm:0.000} < B0 {(macro.TryGetValue("b0", out var x) ? x.ToString("0.000", CultureInfo.InvariantCulture) : "missing")} + 0.15");
        }

        if (!macro.TryGetValue("b1", out var b1v) || warm < b1v + 0.10)
        {
            failures.Add($"D-warm {warm:0.000} < B1 {(macro.TryGetValue("b1", out var y) ? y.ToString("0.000", CultureInfo.InvariantCulture) : "missing")} + 0.10");
        }
    }

    var result = new { date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), guard, limit, passed = failures.Count == 0, failures, systems = report };
    if (options.TryGetValue("out", out var outPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    foreach (var failure in failures)
    {
        Console.Error.WriteLine("FAIL: " + failure);
    }

    return failures.Count == 0 ? 0 : 1;
}

async Task<int> LatencyAsync()
{
    var count = int.Parse(Option("queries", "200"), CultureInfo.InvariantCulture);
    var times = new List<double>();
    for (var i = 0; i < count; i++)
    {
        var started = Stopwatch.GetTimestamp();
        await mcp.CallAsync("prior_art_search", new { question = decisions[i % decisions.Count].Question, limit = 10 }, CancellationToken.None);
        times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    times.Sort();
    var (p50, p95) = (times[times.Count / 2], times[(int)Math.Ceiling(times.Count * 0.95) - 1]);
    Console.WriteLine($"hit latency over {count} queries: p50 {p50:0} ms, p95 {p95:0} ms (limit 1000 ms)");
    return p95 <= 1000 ? 0 : 1;
}

string Option(string name, string fallback) => options.TryGetValue(name, out var v) ? v : fallback;

static Dictionary<string, string> ParseOptions(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        map[args[i].TrimStart('-')] = args[i + 1];
    }

    return map;
}

/// <summary>The 95% Wilson score interval of <paramref name="hits"/> out of <paramref name="n"/>.</summary>
static (double Low, double High) Wilson(int hits, int n)
{
    const double z = 1.96;
    var p = (double)hits / n;
    var centre = p + z * z / (2 * n);
    var margin = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n));
    var denominator = 1 + z * z / n;
    return ((centre - margin) / denominator, (centre + margin) / denominator);
}
