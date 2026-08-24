using Diva.LoadTest;

Console.WriteLine("Diva Agent API Load Tester");

LoadTestOptions opts;
try { opts = LoadTestOptions.Parse(args); }
catch (Exception ex)
{
    Console.Error.WriteLine($"Argument error: {ex.Message}");
    Console.Error.WriteLine("Run with --help for usage.");
    return 1;
}

var client = new DivaApiClient(opts);

// ── Resolve target agent(s) ────────────────────────────────────────────────
if (opts.AgentIds.Count == 0)
{
    Console.WriteLine($"No --agent-id given — discovering an enabled agent from {opts.BaseUrl}/api/agents ...");
    List<AgentSummaryDto> agents;
    try
    {
        using var discoverCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        agents = await client.ListAgentsAsync(discoverCts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not reach the API at {opts.BaseUrl}: {ex.Message}");
        Console.Error.WriteLine("Check --base-url, and --api-key if OAuth is enabled on the target.");
        return 1;
    }

    var candidate = agents.FirstOrDefault(a => a.IsEnabled);
    if (candidate is null)
    {
        Console.Error.WriteLine("No enabled agents found. Pass --agent-id explicitly.");
        return 1;
    }
    opts.AgentIds.Add(candidate.Id);
    Console.WriteLine($"Using agent '{candidate.DisplayName}' ({candidate.Id})");
}

// ── Warmup: pay the cold MCP-connect / cache-population cost once, outside the measured window ──
if (opts.Warmup)
{
    Console.WriteLine("Warming up (1 request per agent, not included in results)...");
    foreach (var agentId in opts.AgentIds)
    {
        var (warmupResult, _) = opts.Mode == RequestMode.Stream
            ? await client.InvokeStreamAsync(agentId, opts.Query, null, TimeSpan.FromSeconds(Math.Max(60, opts.RequestTimeoutSeconds)), 0, 1)
            : await client.InvokeAsync(agentId, opts.Query, null, TimeSpan.FromSeconds(Math.Max(60, opts.RequestTimeoutSeconds)), 0, 1);
        if (!warmupResult.Success)
            Console.WriteLine($"  warning: warmup request for {agentId} failed: {warmupResult.Error}");
    }
}

Console.WriteLine();
var concurrencyDescription = opts.Scenario == ScenarioKind.Ramp
    ? $"Users=0→{opts.RampMaxUsers} (+{opts.RampStepUsers}/{opts.RampStepSeconds}s)"
    : $"Users={opts.Users}";
Console.WriteLine($"Scenario={opts.Scenario}  Mode={opts.Mode}  Agents={opts.AgentIds.Count}  " +
                   $"{concurrencyDescription}  Session={(opts.SharedSessionPerUser ? "shared-per-user" : "new-per-request")}");

var testStart = DateTime.UtcNow;
var results = opts.Scenario switch
{
    ScenarioKind.Burst => await Scenarios.RunBurstAsync(opts, client, opts.AgentIds, testStart),
    ScenarioKind.Soak => await Scenarios.RunSoakAsync(opts, client, opts.AgentIds, testStart),
    ScenarioKind.Ramp => await Scenarios.RunRampAsync(opts, client, opts.AgentIds, testStart),
    _ => throw new InvalidOperationException(),
};
var testEnd = DateTime.UtcNow;

Console.WriteLine();
Console.WriteLine("Test run complete — compiling summary...");
StatsReporter.PrintSummary($"Results — {opts.Scenario} / {opts.Mode}", results, testStart, testEnd);
if (opts.Scenario is ScenarioKind.Soak or ScenarioKind.Ramp)
    StatsReporter.PrintTimeSeries(results);
if (!string.IsNullOrWhiteSpace(opts.OutputCsvPath))
    StatsReporter.WriteCsv(opts.OutputCsvPath, results);

var failureRate = results.Count == 0 ? 1.0 : results.Count(r => !r.Success) / (double)results.Count;
return failureRate > 0.5 ? 2 : 0; // non-zero exit for CI gating on a badly-failed run
